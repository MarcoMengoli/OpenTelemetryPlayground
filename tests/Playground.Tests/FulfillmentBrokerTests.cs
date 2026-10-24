using System.Text;
using Couriers.Api;
using Fulfillment.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Playground.Contracts;
using Playground.Infrastructure;
using RabbitMQ.Client;
using Xunit;

namespace Playground.Tests;

public sealed class FulfillmentBrokerTests
{
    [BrokerFact]
    [Trait("Category", "Broker")]
    public async Task Rabbit_courier_sqlite_retries_deduplicates_and_deadletters_invalid_or_conflicting_messages()
    {
        using var data = new FulfillmentData();
        await using var host = await CourierHost.Start(data, new("temporary-courier-failure", 0, 2, null));
        await using var broker = await FulfillmentBroker.Open();
        var store = data.Store();
        await using var consumer = await broker.Consume(store, host);
        var message = FulfillmentTests.Message();
        await broker.Publish(message);
        await FulfillmentTests.Until(() => store.Get(message.OrderId)?.State == "Ready");
        await broker.Publish(message);
        await broker.Publish(message with { MessageId = Guid.NewGuid() });
        await broker.Publish(message with { MessageId = Guid.NewGuid(), Items = [new("CONFLICT", 1, 1)] });
        await broker.Publish(message with { OrderId = Guid.NewGuid() });
        await broker.Publish(message, type: "Wrong.v1");
        await broker.Publish(message, body: "{}");
        for (var i = 0; i < 4; i++) Assert.NotNull(await broker.Error());
        Assert.Equal(3, data.CourierAttempts(message.OrderId));
        Assert.Equal(1, data.Count("Fulfillment.Service", "SELECT COUNT(*) FROM fulfillment_work"));
        Assert.Equal(2, data.Count("Fulfillment.Service", "SELECT COUNT(*) FROM fulfillment_inbox"));
        var file = Assert.Single(store.UnsentFiles());
        Assert.Equal(message.OrderId, store.Lookup(file.WarehouseReference)!.OrderId);
        Assert.Equal(2, WireJson.Read<WarehouseRequest>(file.PayloadJson).EstimatedDays);
    }

    [BrokerFact]
    [Trait("Category", "Broker")]
    public async Task Exhausted_courier_budget_is_persisted_and_same_message_is_not_retried_on_consumer_restart()
    {
        using var data = new FulfillmentData();
        await using var host = await CourierHost.Start(data, new("temporary-courier-failure", 0, 10, null));
        await using var broker = await FulfillmentBroker.Open();
        var message = FulfillmentTests.Message();
        await using (var first = await broker.Consume(data.Store(), host))
        {
            await broker.Publish(message);
            var failed = await broker.Error();
            Assert.Equal(message.MessageId.ToString(), failed.BasicProperties.MessageId);
        }
        await using var second = await broker.Consume(data.Store(), host);
        await broker.Publish(message);
        await broker.Error();
        var work = data.Store().Get(message.OrderId)!;
        Assert.Equal("Failed", work.State);
        Assert.Equal("courier_http_503", work.LastError);
        Assert.Equal(3, work.Attempts);
        Assert.Equal(3, data.CourierAttempts(message.OrderId));
        Assert.Empty(data.Store().UnsentFiles());
    }

    [BrokerFact]
    [Trait("Category", "Broker")]
    public async Task Consumer_restarts_before_http_after_http_and_after_commit_before_ack_keep_one_outbox()
    {
        using var data = new FulfillmentData();
        await using var host = await CourierHost.Start(data);
        await using var broker = await FulfillmentBroker.Open();
        var store = data.Store();
        var messages = Enumerable.Range(0, 3).Select(_ => FulfillmentTests.Message()).ToArray();
        var references = new List<string>();
        var interruptedChannels = new List<IChannel>();
        try
        {
            for (var boundary = 0; boundary < 3; boundary++)
            {
                var message = messages[boundary];
                await broker.Publish(message);
                // Acquire a real unacknowledged delivery, execute the production stages, then
                // close the channel at a deterministic boundary (equivalent to transport loss).
                var interrupted = await broker.Connection.CreateChannelAsync();
                interruptedChannels.Add(interrupted);
                {
                    var delivery = await interrupted.BasicGetAsync(broker.Queue, false);
                    Assert.NotNull(delivery);
                    Assert.Equal(message.MessageId.ToString(), delivery.BasicProperties.MessageId);
                    store.Accept(WireJson.Read<FulfillOrder>(Encoding.UTF8.GetString(delivery.Body.Span)));
                    references.Add(store.Get(message.OrderId)!.WarehouseReference);
                    if (boundary == 1)
                    {
                        store.BeginAttempt(message.OrderId);
                        using var response = await host.Post(new DeliveryEstimateRequest(message.OrderId, message.ShippingAddress));
                        response.EnsureSuccessStatusCode();
                        // Courier committed; Fulfillment has not persisted the response.
                    }
                    if (boundary == 2) Assert.True(await host.Processor(store).ProcessAsync(message.OrderId, CancellationToken.None));
                }
            }
        }
        finally { foreach (var interrupted in interruptedChannels) await interrupted.DisposeAsync(); }
        store = data.Store();
        await using var restarted = await broker.Consume(store, host);
        await FulfillmentTests.Until(() => store.UnsentFiles().Count == 3);
        // Sentinel guarantees all redeliveries preceding it were handled and acked.
        var sentinel = FulfillmentTests.Message();
        await broker.Publish(sentinel);
        await FulfillmentTests.Until(() => store.Get(sentinel.OrderId)?.State == "Ready");
        for (var index = 0; index < messages.Length; index++)
        {
            var work = store.Get(messages[index].OrderId)!;
            Assert.Equal("Ready", work.State);
            Assert.Equal(references[index], work.WarehouseReference);
            Assert.Equal(index == 1 ? 2 : 1, data.CourierAttempts(messages[index].OrderId));
        }
        Assert.Equal(4, store.UnsentFiles().Count);
        Assert.Null(await broker.Channel.BasicGetAsync(broker.Errors, true));
    }

    [BrokerFact]
    [Trait("Category", "Broker")]
    public async Task Real_timeout_then_consumer_restart_recovers_pending_work_without_changing_reference()
    {
        using var data = new FulfillmentData();
        await using var broker = await FulfillmentBroker.Open();
        var message = FulfillmentTests.Message();
        var store = data.Store();
        string reference;
        await using (var slow = await CourierHost.Start(data, new("slow-courier", 6000, 0, null)))
        {
            await using (var first = await broker.Consume(store, slow))
            {
                await broker.Publish(message);
                await FulfillmentTests.Until(() => store.Get(message.OrderId)?.LastError == "courier_timeout");
                reference = store.Get(message.OrderId)!.WarehouseReference;
            }
            Assert.Equal("Pending", store.Get(message.OrderId)!.State);
            Assert.Empty(store.UnsentFiles());
        }
        await using var fast = await CourierHost.Start(data);
        await using var restarted = await broker.Consume(data.Store(), fast);
        await FulfillmentTests.Until(() => data.Store().Get(message.OrderId)?.State == "Ready");
        Assert.Equal(reference, Assert.Single(store.UnsentFiles()).WarehouseReference);
        Assert.Equal(2, data.CourierAttempts(message.OrderId));
        Assert.Equal(2, store.Get(message.OrderId)!.Attempts);
        Assert.Null(await broker.Channel.BasicGetAsync(broker.Errors, true));
    }
}

internal sealed class FulfillmentBroker(IConnection connection, IChannel channel, string exchange) : IAsyncDisposable
{
    public IConnection Connection { get; } = connection;
    public IChannel Channel { get; } = channel;
    public string Queue => exchange + ".fulfill";
    public string Errors => exchange + ".errors";
    public static async Task<FulfillmentBroker> Open()
    {
        var settings = LabSettings.Read(new ConfigurationBuilder().AddEnvironmentVariables().Build());
        var connection = await Broker.Factory(settings).CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        var exchange = settings.Name + ".us07." + Guid.NewGuid().ToString("N");
        var result = new FulfillmentBroker(connection, channel, exchange);
        await channel.ExchangeDeclareAsync(exchange, ExchangeType.Direct, false, false);
        await channel.QueueDeclareAsync(result.Errors, false, true, false);
        await channel.QueueBindAsync(result.Errors, exchange, "error");
        await channel.QueueDeclareAsync(result.Queue, false, true, false, new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = exchange, ["x-dead-letter-routing-key"] = "error"
        });
        await channel.QueueBindAsync(result.Queue, exchange, "fulfill");
        return result;
    }
    public async Task Publish(FulfillOrder message, string type = "FulfillOrder.v1", string? body = null)
    {
        var properties = new BasicProperties { MessageId = message.MessageId.ToString(), ContentType = "application/json", Persistent = true, Type = type };
        await Channel.BasicPublishAsync(exchange, "fulfill", true, properties, Encoding.UTF8.GetBytes(body ?? WireJson.Write(message)));
    }
    public async Task<BasicGetResult> Error()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (true)
        {
            var message = await Channel.BasicGetAsync(Errors, true, timeout.Token);
            if (message is not null) return message;
            await Task.Delay(25, timeout.Token);
        }
    }
    public async Task<FulfillmentTestConsumer> Consume(FulfillmentStore store, CourierHost host)
    {
        var channel = await Connection.CreateChannelAsync();
        var stop = new CancellationTokenSource();
        var consumer = new FulfillmentConsumer(store, host.Processor(store), NullLogger<FulfillmentConsumer>.Instance);
        return new(channel, stop, consumer.RunAsync(channel, Queue, stop.Token));
    }
    public async ValueTask DisposeAsync()
    {
        if (Channel.IsOpen)
        {
            await Channel.QueueDeleteAsync(Queue);
            await Channel.QueueDeleteAsync(Errors);
            await Channel.ExchangeDeleteAsync(exchange);
        }
        await Channel.DisposeAsync();
        await Connection.DisposeAsync();
    }
}

internal sealed class FulfillmentTestConsumer(IChannel channel, CancellationTokenSource stop, Task run) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        try { await run; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            await channel.DisposeAsync();
            stop.Dispose();
        }
    }
}