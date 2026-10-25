using System.Text;
using Playground.Contracts;
using Playground.Infrastructure;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Orders.Service;

public sealed class ShipmentConsumer(LabSettings settings, OrdersStore store, ShipmentProcessor processor, ILogger<ShipmentConsumer> logger) : BackgroundService
{
    public static ShipmentConfirmation Read(ReadOnlyMemory<byte> body, IReadOnlyBasicProperties properties)
    {
        var message = WireJson.Read<ShipmentConfirmation>(new UTF8Encoding(false, true).GetString(body.Span));
        if (properties.ContentType != "application/json" || properties.Type != "ShipmentConfirmation.v1" || !properties.Persistent ||
            !Guid.TryParse(properties.MessageId, out var id) || id != message.MessageId)
            throw new ArgumentException("AMQP properties do not match ShipmentConfirmation.v1.");
        return message;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var message in store.PendingShipments())
        {
            try { await processor.ProcessAsync(message, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogError(exception, "Shipment recovery failed for {OrderId}; durable work retained", message.OrderId); }
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
                    catch (Exception exception) { logger.LogError(exception, "Shipment rejected, message {MessageId}", delivery.BasicProperties.MessageId); }
                    if (stoppingToken.IsCancellationRequested || !channel.IsOpen) return;
                    try
                    {
                        if (completed) await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                        else await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: false, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                    catch (Exception exception) { logger.LogWarning(exception, "Shipment acknowledgement failed; delivery may repeat for {MessageId}", delivery.BasicProperties.MessageId); }
                };
                await channel.BasicConsumeAsync(settings.Name + ".orders.shipment-confirmation.v1", autoAck: false, consumer, stoppingToken);
                await disconnected.Task.WaitAsync(stoppingToken);
                logger.LogWarning("Shipment consumer disconnected, connection failure {Attempt}", ++failures);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogWarning(exception, "Shipment consumer connection failed, attempt {Attempt}", ++failures); }
            if (failures >= 3) return;
            await Task.Delay(TimeSpan.FromSeconds(1 << (failures - 1)), stoppingToken);
        }
    }
}
