using RabbitMQ.Client;

namespace Playground.Infrastructure;

public static class Broker
{
    public static ConnectionFactory Factory(LabSettings settings) => new()
    {
        HostName = settings.RabbitHost, Port = settings.RabbitPort,
        UserName = settings.RabbitUser, Password = settings.RabbitPassword, VirtualHost = settings.VirtualHost,
        RequestedConnectionTimeout = TimeSpan.FromSeconds(5), AutomaticRecoveryEnabled = true
    };

    // Explicit routing contract; consumers and outbox dispatchers belong to later stories.
    public static readonly (string Queue, string RoutingKey)[] Routes =
    [
        ("orders.order-created.v1", "order.created.v1"),
        ("inventory.reserve.v1", "inventory.reserve.v1"),
        ("fulfillment.fulfill.v1", "fulfillment.fulfill.v1"),
        ("orders.shipment-confirmation.v1", "shipment.confirmed.v1")
    ];

    public static async Task DeclareAsync(IChannel channel, LabSettings settings, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(settings.Exchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(settings.ErrorExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        foreach (var (suffix, routingKey) in Routes)
        {
            var queue = $"{settings.Name}.{suffix}";
            await channel.QueueDeclareAsync(queue + ".errors", durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
            await channel.QueueBindAsync(queue + ".errors", settings.ErrorExchange, routingKey, cancellationToken: cancellationToken);
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = settings.ErrorExchange, ["x-dead-letter-routing-key"] = routingKey }, cancellationToken: cancellationToken);
            await channel.QueueBindAsync(queue, settings.Exchange, routingKey, cancellationToken: cancellationToken);
        }
    }
}
