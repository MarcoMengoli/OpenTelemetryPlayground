using Playground.Infrastructure;

namespace Fulfillment.Service;

public sealed class FulfillmentWorker(LabSettings settings, FulfillmentConsumer consumer, ILogger<FulfillmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Recreate the whole session after failure; do not stack auto-recovery consumers.
                var factory = Broker.Factory(settings);
                factory.AutomaticRecoveryEnabled = false;
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await Broker.DeclareAsync(channel, settings, stoppingToken);
                await consumer.RunAsync(channel, settings.Name + ".fulfillment.fulfill.v1", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                logger.LogError(e, "Fulfillment session failed; unacknowledged work remains recoverable");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }
    }
}