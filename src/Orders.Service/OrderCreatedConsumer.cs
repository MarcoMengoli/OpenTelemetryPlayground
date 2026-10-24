using System.Text;
using System.Text.Json;
using Playground.Contracts;
using Playground.Infrastructure;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Orders.Service;

public sealed class OrderCreatedConsumer(LabSettings settings, OrdersStore store, OrderIntake intake, ILogger<OrderCreatedConsumer> logger) : BackgroundService
{
    public static OrderCreated Read(ReadOnlyMemory<byte> body, IReadOnlyBasicProperties properties)
    {
        var message = WireJson.Read<OrderCreated>(new UTF8Encoding(false, true).GetString(body.Span));
        if (properties.ContentType != "application/json" || properties.Type != "OrderCreated.v1" ||
            !properties.Persistent || !Guid.TryParse(properties.MessageId, out var id) || id != message.MessageId)
            throw new ArgumentException("AMQP properties do not match OrderCreated.v1.");
        return message;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A restart is the explicit recovery action for durable pending authorizations.
        foreach (var message in store.StartRecoveryCycle())
        {
            try { await intake.ProcessAsync(message, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogError(exception, "Recovery could not process {OrderId}; durable work retained", message.OrderId); }
        }
        var connectionFailures = 0;
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
                    try
                    {
                        var message = Read(delivery.Body, delivery.BasicProperties);
                        var completed = await intake.ProcessAsync(message, stoppingToken);
                        if (completed) await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
                        else await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* Leave unacked for redelivery. */ }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "OrderCreated rejected; message {MessageId} goes to the configured error queue", delivery.BasicProperties.MessageId);
                        if (channel.IsOpen) await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, stoppingToken);
                    }
                };
                await channel.BasicConsumeAsync(settings.Name + ".orders.order-created.v1", autoAck: false, consumer, stoppingToken);
                await disconnected.Task.WaitAsync(stoppingToken);
                logger.LogWarning("Orders consumer disconnected, connection failure {Attempt}", ++connectionFailures);
                if (connectionFailures >= 3) return;
                await Task.Delay(TimeSpan.FromSeconds(1 << (connectionFailures - 1)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Orders consumer connection failed, attempt {Attempt}", ++connectionFailures);
                if (connectionFailures >= 3) return;
                await Task.Delay(TimeSpan.FromSeconds(1 << (connectionFailures - 1)), stoppingToken);
            }
        }
    }
}
