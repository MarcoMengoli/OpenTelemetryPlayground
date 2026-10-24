using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Playground.Infrastructure;

public sealed class OutboxPump(LabSettings settings, DurableOutbox outbox, ILogger<OutboxPump> logger) : BackgroundService
{
    public async Task<bool> SendNextAsync(ConfirmedPublisher publisher, CancellationToken token)
    {
        var message = outbox.Next();
        if (message is null) return false;
        var attempt = outbox.StartAttempt(message.MessageId);
        if (attempt == 0) return true;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await publisher.PublishAsync(message, deadline.Token);
            outbox.MarkSent(message.MessageId);
            logger.LogInformation("Published {MessageType} for {OrderId}, message {MessageId}", message.Type, message.OrderId, message.MessageId);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            outbox.MarkFailed(message.MessageId, attempt);
            logger.LogWarning(exception, "Outbox publish failed for {MessageId}, attempt {Attempt}; message remains durable", message.MessageId, attempt);
            await publisher.DisposeAsync();
            await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), token);
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        outbox.StartCycle();
        await using var publisher = new ConfirmedPublisher(settings);
        var storageFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await SendNextAsync(publisher, stoppingToken)) await Task.Delay(250, stoppingToken);
                storageFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Outbox storage failure, attempt {Attempt}; restart required after three failures", ++storageFailures);
                if (storageFailures >= 3) return;
                await Task.Delay(TimeSpan.FromSeconds(1 << (storageFailures - 1)), stoppingToken);
            }
        }
    }
}
