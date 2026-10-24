using System.Text;
using Playground.Contracts;
using Playground.Infrastructure;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Inventory.Service;

public sealed class InventoryConsumer(LabSettings settings, InventoryStore store, InventoryProcessor processor, ILogger<InventoryConsumer> logger) : BackgroundService
{
    public static ReserveInventory Read(ReadOnlyMemory<byte> body, IReadOnlyBasicProperties properties)
    {
        var message = WireJson.Read<ReserveInventory>(new UTF8Encoding(false, true).GetString(body.Span));
        if (properties.ContentType != "application/json" || properties.Type != "ReserveInventory.v1" ||
            !properties.Persistent || !Guid.TryParse(properties.MessageId, out var id) || id != message.MessageId)
            throw new ArgumentException("AMQP properties do not match ReserveInventory.v1.");
        return message;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var message in store.Pending())
        {
            try { await processor.ProcessAsync(message, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogError(exception, "Inventory recovery failed for {OrderId}; durable work retained", message.OrderId); }
        }
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var factory = Broker.Factory(settings);
                factory.AutomaticRecoveryEnabled = false;
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await Broker.DeclareAsync(channel, settings, stoppingToken);
                await channel.BasicQosAsync(0, 1, false, stoppingToken);
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.ConnectionShutdownAsync += (_, _) => { disconnected.TrySetResult(); return Task.CompletedTask; };
                channel.ChannelShutdownAsync += (_, _) => { disconnected.TrySetResult(); return Task.CompletedTask; };
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    var completed = false;
                    try { completed = await processor.ProcessAsync(Read(delivery.Body, delivery.BasicProperties), stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception exception) { logger.LogError(exception, "Inventory rejected message {MessageId}", delivery.BasicProperties.MessageId); }
                    if (stoppingToken.IsCancellationRequested || !channel.IsOpen) return;
                    try
                    {
                        if (completed) await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
                        else await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                    catch (Exception exception) { logger.LogWarning(exception, "Inventory acknowledgement failed; delivery may repeat for {MessageId}", delivery.BasicProperties.MessageId); }
                };
                await channel.BasicConsumeAsync(settings.Name + ".inventory.reserve.v1", autoAck: false, consumer, stoppingToken);
                await disconnected.Task.WaitAsync(stoppingToken);
                logger.LogWarning("Inventory consumer disconnected, connection failure {Attempt}", ++failures);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogWarning(exception, "Inventory consumer connection failed, attempt {Attempt}", ++failures); }
            if (failures >= 3) return;
            await Task.Delay(TimeSpan.FromSeconds(1 << (failures - 1)), stoppingToken);
        }
    }
}
