using System.Text;
using System.Text.Json;
using Playground.Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Fulfillment.Service;

public sealed class FulfillmentConsumer(FulfillmentStore store, FulfillmentProcessor processor, ILogger<FulfillmentConsumer> logger)
{
    public async Task RunAsync(IChannel channel, string queue, CancellationToken token)
    {
        // Recover durable work even when the broker delivery was lost after a committed acceptance.
        await processor.ResumePendingAsync(token);
        await channel.BasicQosAsync(0, 1, false, token);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => stopped.TrySetCanceled(token));
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ShutdownAsync += (_, _) => { stopped.TrySetException(new IOException("Fulfillment channel closed.")); return Task.CompletedTask; };
        consumer.UnregisteredAsync += (_, _) => { stopped.TrySetException(new IOException("Fulfillment consumer unregistered.")); return Task.CompletedTask; };
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                FulfillOrder message;
                try
                {
                    message = WireJson.Read<FulfillOrder>(new UTF8Encoding(false, true).GetString(delivery.Body.Span));
                    if (delivery.BasicProperties.Type != "FulfillOrder.v1" ||
                        delivery.BasicProperties.ContentType != "application/json" ||
                        !Guid.TryParse(delivery.BasicProperties.MessageId, out var id) || id != message.MessageId ||
                        !delivery.BasicProperties.Persistent)
                        throw new ArgumentException("AMQP envelope does not match FulfillOrder.");
                    store.Accept(message);
                }
                catch (Exception e) when (e is JsonException or ArgumentException or FulfillmentConflictException)
                {
                    logger.LogWarning("Rejecting invalid or conflicting FulfillOrder: {Reason}", e.Message);
                    await channel.BasicNackAsync(delivery.DeliveryTag, false, false, token);
                    return;
                }
                var ready = await processor.ProcessAsync(message.OrderId, token);
                if (ready) await channel.BasicAckAsync(delivery.DeliveryTag, false, token);
                else
                {
                    logger.LogWarning("Fulfillment failed for {OrderId}; delivery moved to errors", message.OrderId);
                    await channel.BasicNackAsync(delivery.DeliveryTag, false, false, token);
                }
            }
            catch (Exception e)
            {
                // No ack/nack on infrastructure failure: caller closes channel and retries the session.
                stopped.TrySetException(e);
            }
        };
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, token);
        await stopped.Task;
    }
}