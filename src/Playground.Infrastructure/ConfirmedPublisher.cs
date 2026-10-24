using System.Text;
using RabbitMQ.Client;

namespace Playground.Infrastructure;

// One sequential dispatcher owns this connection/channel. No implicit application headers.
public sealed class ConfirmedPublisher(LabSettings settings) : IAsyncDisposable
{
    private IConnection? connection;
    private IChannel? channel;

    public async Task PublishAsync(OutboxMessage message, CancellationToken token)
    {
        if (channel?.IsOpen != true)
        {
            await DisposeAsync();
            var factory = Broker.Factory(settings);
            factory.AutomaticRecoveryEnabled = false; // The durable dispatcher owns retry budgets.
            connection = await factory.CreateConnectionAsync(token);
            channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), token);
            await Broker.DeclareAsync(channel, settings, token);
        }
        var properties = new BasicProperties { MessageId = message.MessageId.ToString(), ContentType = "application/json", Persistent = true, Type = message.Type };
        // Return/nack/cancellation/disconnection throw; only a completed confirm permits MarkSent.
        await channel.BasicPublishAsync(settings.Exchange, message.RoutingKey, mandatory: true, properties, Encoding.UTF8.GetBytes(message.Body), token);
    }

    public async ValueTask DisposeAsync()
    {
        var previousChannel = channel;
        var previousConnection = connection;
        channel = null;
        connection = null;
        try { if (previousChannel is not null) await previousChannel.DisposeAsync(); }
        finally { if (previousConnection is not null) await previousConnection.DisposeAsync(); }
    }
}
