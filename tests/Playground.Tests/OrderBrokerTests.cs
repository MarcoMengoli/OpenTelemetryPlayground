using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Checkout.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Orders.Service;
using Playground.Contracts;
using Playground.Infrastructure;
using RabbitMQ.Client;
using Xunit;

namespace Playground.Tests;

[Trait("Category", "Broker")]
public sealed class OrderBrokerTests
{
    [BrokerFact]
    public async Task Real_checkout_orders_payments_publish_both_commands_and_deduplicate_deliveries()
    {
        await using var lab = await OrderBrokerLab.Create();
        using var paymentData = new PaymentData();
        await using var payments = await PaymentHost.Start(paymentData);
        await using var orders = await lab.Orders(payments.Client.BaseAddress!);
        await using var checkout = await lab.Checkout(orders.Client.BaseAddress!);
        var request = OrderTestData.Request();
        using var accepted = await checkout.Client.PostAsJsonAsync("/api/v1/orders", request, WireJson.Options);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var snapshot = await lab.Snapshot(checkout, request.OrderId, x => x.Status == OrderStatus.FulfillmentPending);
        var paid = await payments.Snapshot(request.OrderId);
        Assert.Equal(paid.AuthorizationId, snapshot.AuthorizationId);
        Assert.Equal(PaymentStatus.Authorized, paid.Status);
        await using var connection = await Broker.Factory(lab.Settings).CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var reserved = await OrderBrokerLab.Receive(channel, lab.Settings.Name + ".inventory.reserve.v1");
        var fulfilled = await OrderBrokerLab.Receive(channel, lab.Settings.Name + ".fulfillment.fulfill.v1");
        var reserve = WireJson.Read<ReserveInventory>(Encoding.UTF8.GetString(reserved.Body.Span));
        var fulfill = WireJson.Read<FulfillOrder>(Encoding.UTF8.GetString(fulfilled.Body.Span));
        Assert.Equal(request.OrderId, reserve.OrderId);
        Assert.Equal(request.OrderId, fulfill.OrderId);
        Assert.NotEqual(reserve.MessageId, fulfill.MessageId);
        Assert.Equal(reserve.MessageId.ToString(), reserved.BasicProperties.MessageId);
        Assert.Equal(fulfill.MessageId.ToString(), fulfilled.BasicProperties.MessageId);
        Assert.Equal(request.ShippingAddress, fulfill.ShippingAddress);

        var source = WireJson.Read<OrderCreated>((string)OrderTestData.Query(lab.Settings, "Checkout.Api", "SELECT body FROM outbox LIMIT 1")!);
        await using var publisher = new ConfirmedPublisher(lab.Settings);
        await publisher.PublishAsync(new(source.MessageId, source.OrderId, "OrderCreated.v1", "order.created.v1", WireJson.Write(source)), CancellationToken.None);
        var logicalDuplicate = source with { MessageId = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow };
        await publisher.PublishAsync(new(logicalDuplicate.MessageId, logicalDuplicate.OrderId, "OrderCreated.v1", "order.created.v1", WireJson.Write(logicalDuplicate)), CancellationToken.None);
        await lab.Count("Orders.Service", "SELECT COUNT(*) FROM order_inbox WHERE processed = 1", 2);
        Assert.Equal(2L, OrderTestData.Query(lab.Settings, "Orders.Service", "SELECT COUNT(*) FROM outbox"));
        Assert.Equal(1, paymentData.Count("SELECT COUNT(*) FROM payments"));
        Assert.Null(await channel.BasicGetAsync(lab.Settings.Name + ".inventory.reserve.v1", autoAck: true));
        Assert.Null(await channel.BasicGetAsync(lab.Settings.Name + ".fulfillment.fulfill.v1", autoAck: true));
    }

    [BrokerFact]
    public async Task Business_decline_produces_no_downstream_commands()
    {
        await using var lab = await OrderBrokerLab.Create();
        var request = OrderTestData.Request();
        using var paymentData = new PaymentData();
        await using var payments = await PaymentHost.Start(paymentData, new("payment-declined", 0, 0, request.OrderId));
        await using var orders = await lab.Orders(payments.Client.BaseAddress!);
        await using var checkout = await lab.Checkout(orders.Client.BaseAddress!);
        using var accepted = await checkout.Client.PostAsJsonAsync("/api/v1/orders", request, WireJson.Options);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var snapshot = await lab.Snapshot(checkout, request.OrderId, x => x.Status == OrderStatus.PaymentDeclined);
        Assert.Equal("simulated_decline", snapshot.FailureCode);
        Assert.Null(snapshot.AuthorizationId);
        Assert.Null(snapshot.LastError);
        Assert.Equal(0L, OrderTestData.Query(lab.Settings, "Orders.Service", "SELECT COUNT(*) FROM outbox"));
        await using var connection = await Broker.Factory(lab.Settings).CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        Assert.Null(await channel.BasicGetAsync(lab.Settings.Name + ".inventory.reserve.v1", autoAck: true));
        Assert.Null(await channel.BasicGetAsync(lab.Settings.Name + ".fulfillment.fulfill.v1", autoAck: true));
    }

    [BrokerFact]
    public async Task Mandatory_return_and_interruption_between_publishes_preserve_each_outbox_identity()
    {
        await using var lab = await OrderBrokerLab.Create();
        var checkout = new CheckoutStore(lab.Settings);
        checkout.Initialize();
        checkout.Accept(OrderTestData.Request());
        var checkoutOutbox = new DurableOutbox(checkout.Database);
        var original = checkoutOutbox.Next()!;
        // Inject only a routing fault in this test-owned DB. Mandatory return must
        // leave the original body and MessageId durable rather than mark it sent.
        OrderTestData.Query(lab.Settings, "Checkout.Api", "UPDATE outbox SET routing_key = 'test.unroutable.v1'");
        await using var publisher = new ConfirmedPublisher(lab.Settings);
        var checkoutPump = new OutboxPump(lab.Settings, checkoutOutbox, NullLogger<OutboxPump>.Instance);
        Assert.True(await checkoutPump.SendNextAsync(publisher, CancellationToken.None));
        Assert.Equal(0L, OrderTestData.Query(lab.Settings, "Checkout.Api", "SELECT sent FROM outbox"));
        Assert.Equal(original.Body, checkoutOutbox.Next()!.Body);
        Assert.Equal(original.MessageId, checkoutOutbox.Next()!.MessageId);
        OrderTestData.Query(lab.Settings, "Checkout.Api", "UPDATE outbox SET routing_key = 'order.created.v1'");
        var reopenedCheckout = new CheckoutStore(lab.Settings);
        reopenedCheckout.Initialize();
        checkoutOutbox.StartCycle();
        Assert.True(await checkoutPump.SendNextAsync(publisher, CancellationToken.None));
        await using var connection = await Broker.Factory(lab.Settings).CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var received = await OrderBrokerLab.Receive(channel, lab.Settings.Name + ".orders.order-created.v1");
        Assert.Equal(original.MessageId.ToString(), received.BasicProperties.MessageId);
        Assert.Equal(original.Body, Encoding.UTF8.GetString(received.Body.Span));
        var source = WireJson.Read<OrderCreated>(original.Body);
        var store = new OrdersStore(lab.Settings);
        store.Initialize();
        using var paymentData = new PaymentData();
        await using var payments = await PaymentHost.Start(paymentData);
        Assert.True(await OrderTestData.Intake(store, payments.Client.BaseAddress!).ProcessAsync(source, CancellationToken.None));
        var outbox = new DurableOutbox(store.Database);
        var pump = new OutboxPump(lab.Settings, outbox, NullLogger<OutboxPump>.Instance);
        Assert.True(await pump.SendNextAsync(publisher, CancellationToken.None));
        _ = await OrderBrokerLab.Receive(channel, lab.Settings.Name + ".inventory.reserve.v1");
        var second = outbox.Next()!;
        Assert.Equal("FulfillOrder.v1", second.Type);
        await using (var unavailable = new ConfirmedPublisher(lab.Settings with { RabbitPort = 1 }))
            for (var attempt = 0; attempt < 3; attempt++) Assert.True(await pump.SendNextAsync(unavailable, CancellationToken.None));
        Assert.Null(outbox.Next());
        Assert.Equal("publish_retry_exhausted", outbox.Status(source.OrderId).LastError);
        Assert.Equal(1L, OrderTestData.Query(lab.Settings, "Orders.Service", "SELECT COUNT(*) FROM outbox WHERE sent = 0"));
        var restartedStore = new OrdersStore(lab.Settings);
        restartedStore.Initialize();
        var restartedOutbox = new DurableOutbox(restartedStore.Database);
        restartedOutbox.StartCycle();
        Assert.Null(restartedOutbox.Next());
        Assert.Equal(3L, OrderTestData.Query(lab.Settings, "Orders.Service", "SELECT attempts FROM outbox WHERE sent = 0"));
        OrderTestData.Query(lab.Settings, "Orders.Service", "UPDATE outbox SET attempts = 0, last_error = NULL WHERE sent = 0");
        restartedOutbox.StartCycle();
        Assert.Equal(second, restartedOutbox.Next());
        Assert.True(await new OutboxPump(lab.Settings, restartedOutbox, NullLogger<OutboxPump>.Instance).SendNextAsync(publisher, CancellationToken.None));
        var fulfillment = await OrderBrokerLab.Receive(channel, lab.Settings.Name + ".fulfillment.fulfill.v1");
        Assert.Equal(second.MessageId.ToString(), fulfillment.BasicProperties.MessageId);
        Assert.Equal(second.Body, Encoding.UTF8.GetString(fulfillment.Body.Span));
        Assert.Null(await channel.BasicGetAsync(lab.Settings.Name + ".inventory.reserve.v1", autoAck: true));
        Assert.Null(restartedOutbox.Next());
        Assert.Null(restartedStore.Get(source.OrderId)!.LastError);
    }

    [BrokerFact]
    public async Task Real_five_second_HTTP_timeouts_dead_letter_once_and_restart_resumes_durable_work()
    {
        await using var lab = await OrderBrokerLab.Create();
        using var paymentData = new PaymentData();
        var request = OrderTestData.Request();
        PaymentHost? payments = null;
        OrderTestHost? orders = null;
        OrderTestHost? checkout = null;
        try
        {
            payments = await PaymentHost.Start(paymentData, new("happy-path", 6000, 0, request.OrderId));
            orders = await lab.Orders(payments.Client.BaseAddress!);
            checkout = await lab.Checkout(orders.Client.BaseAddress!);
            using var accepted = await checkout.Client.PostAsJsonAsync("/api/v1/orders", request, WireJson.Options);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var pending = await lab.Snapshot(checkout, request.OrderId, x => x.LastError == "payment_retry_exhausted");
            Assert.Equal(OrderStatus.Created, pending.Status);
            Assert.Null(pending.FailureCode);
            var remote = await payments.Snapshot(request.OrderId);
            Assert.Equal(PaymentStatus.Authorized, remote.Status);
            await using var connection = await Broker.Factory(lab.Settings).CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            _ = await OrderBrokerLab.Receive(channel, lab.Settings.Name + ".orders.order-created.v1.errors");
            Assert.Equal(3L, OrderTestData.Query(lab.Settings, "Orders.Service", "SELECT auth_attempts FROM orders"));
            Assert.Equal(0L, OrderTestData.Query(lab.Settings, "Orders.Service", "SELECT COUNT(*) FROM outbox"));
            await checkout.DisposeAsync(); checkout = null;
            await orders.DisposeAsync(); orders = null;
            await payments.DisposeAsync(); payments = null;
            // Explicit operator recovery, with all writers stopped. A process
            // restart alone is not permission to reset a durable spent budget.
            var paused = new OrdersStore(lab.Settings);
            paused.Initialize();
            Assert.Empty(paused.StartRecoveryCycle());
            OrderTestData.Query(lab.Settings, "Orders.Service", "UPDATE orders SET auth_attempts = 0, last_error = NULL WHERE status = 'Created' AND authorization_blocked = 0");
            payments = await PaymentHost.Start(paymentData);
            orders = await lab.Orders(payments.Client.BaseAddress!);
            checkout = await lab.Checkout(orders.Client.BaseAddress!);
            var recovered = await lab.Snapshot(checkout, request.OrderId, x => x.Status == OrderStatus.FulfillmentPending);
            Assert.Equal(remote.AuthorizationId, recovered.AuthorizationId);
            _ = await OrderBrokerLab.Receive(channel, lab.Settings.Name + ".inventory.reserve.v1");
            _ = await OrderBrokerLab.Receive(channel, lab.Settings.Name + ".fulfillment.fulfill.v1");
            Assert.Equal(1, paymentData.Count("SELECT COUNT(*) FROM payments"));
            Assert.Equal(1L, OrderTestData.Query(lab.Settings, "Orders.Service", "SELECT processed FROM order_inbox"));
        }
        finally
        {
            if (checkout is not null) await checkout.DisposeAsync();
            if (orders is not null) await orders.DisposeAsync();
            if (payments is not null) await payments.DisposeAsync();
        }
    }

    [BrokerFact]
    public async Task Stop_during_HTTP_after_remote_commit_resumes_with_remaining_budget_and_stable_authorization()
    {
        await using var lab = await OrderBrokerLab.Create();
        using var paymentData = new PaymentData();
        var request = OrderTestData.Request();
        await using var payments = await PaymentHost.Start(paymentData, new("happy-path", 2000, 0, request.OrderId));
        OrderTestHost? orders = null;
        OrderTestHost? checkout = null;
        try
        {
            orders = await lab.Orders(payments.Client.BaseAddress!);
            checkout = await lab.Checkout(orders.Client.BaseAddress!);
            using var accepted = await checkout.Client.PostAsJsonAsync("/api/v1/orders", request, WireJson.Options);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                while (paymentData.Count("SELECT COUNT(*) FROM payments") == 0) await Task.Delay(10, deadline.Token);
            }
            var original = await payments.Snapshot(request.OrderId);
            Assert.Equal(1L, OrderTestData.Query(lab.Settings, "Orders.Service", "SELECT auth_attempts FROM orders"));
            await checkout.DisposeAsync(); checkout = null;
            await orders.DisposeAsync(); orders = null;
            Assert.Equal(0L, OrderTestData.Query(lab.Settings, "Orders.Service", "SELECT COUNT(*) FROM outbox"));
            Assert.Equal(0L, OrderTestData.Query(lab.Settings, "Orders.Service", "SELECT processed FROM order_inbox"));
            orders = await lab.Orders(payments.Client.BaseAddress!);
            checkout = await lab.Checkout(orders.Client.BaseAddress!);
            var recovered = await lab.Snapshot(checkout, request.OrderId, x => x.Status == OrderStatus.FulfillmentPending);
            Assert.Equal(original.AuthorizationId, recovered.AuthorizationId);
            Assert.Equal(2L, OrderTestData.Query(lab.Settings, "Orders.Service", "SELECT auth_attempts FROM orders"));
            Assert.Equal(1, paymentData.Count("SELECT COUNT(*) FROM payments"));
            await using var connection = await Broker.Factory(lab.Settings).CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            _ = await OrderBrokerLab.Receive(channel, lab.Settings.Name + ".inventory.reserve.v1");
            _ = await OrderBrokerLab.Receive(channel, lab.Settings.Name + ".fulfillment.fulfill.v1");
        }
        finally
        {
            if (checkout is not null) await checkout.DisposeAsync();
            if (orders is not null) await orders.DisposeAsync();
        }
    }

    [BrokerFact]
    public async Task Invalid_messages_go_to_error_queue_without_acquiring_an_order()
    {
        await using var lab = await OrderBrokerLab.Create();
        using var paymentData = new PaymentData();
        await using var payments = await PaymentHost.Start(paymentData);
        await using var orders = await lab.Orders(payments.Client.BaseAddress!);
        await using var connection = await Broker.Factory(lab.Settings).CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        var properties = new BasicProperties { MessageId = Guid.NewGuid().ToString(), ContentType = "application/json", Persistent = true, Type = "OrderCreated.v1" };
        await channel.BasicPublishAsync(lab.Settings.Exchange, "order.created.v1", true, properties, Encoding.UTF8.GetBytes("{}"));
        var mismatched = OrderTestData.Message();
        await channel.BasicPublishAsync(lab.Settings.Exchange, "order.created.v1", true, properties, Encoding.UTF8.GetBytes(WireJson.Write(mismatched)));
        _ = await OrderBrokerLab.Receive(channel, lab.Settings.Name + ".orders.order-created.v1.errors");
        _ = await OrderBrokerLab.Receive(channel, lab.Settings.Name + ".orders.order-created.v1.errors");
        Assert.Equal(0L, OrderTestData.Query(lab.Settings, "Orders.Service", "SELECT COUNT(*) FROM orders"));
        Assert.Equal(0, paymentData.Count("SELECT COUNT(*) FROM payments"));
    }
}

internal sealed class OrderBrokerLab(LabSettings settings, HttpClient management) : IAsyncDisposable
{
    public LabSettings Settings { get; } = settings;
    private bool created;
    public static async Task<OrderBrokerLab> Create()
    {
        var source = LabSettings.Read(new ConfigurationBuilder().AddEnvironmentVariables().Build());
        var name = "us04-" + Guid.NewGuid().ToString("N")[..20];
        var settings = source with { Name = name, Root = Path.Combine(Path.GetTempPath(), "playground-" + name) };
        var management = new HttpClient { BaseAddress = new Uri($"http://{source.RabbitHost}:{source.RabbitPort + 1}"), Timeout = TimeSpan.FromSeconds(10) };
        management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(source.RabbitUser + ":" + source.RabbitPassword)));
        var lab = new OrderBrokerLab(settings, management);
        try
        {
            using var created = await management.PutAsJsonAsync("/api/vhosts/" + name, new { });
            created.EnsureSuccessStatusCode();
            lab.created = true;
            using var permission = await management.PutAsJsonAsync("/api/permissions/" + name + "/" + Uri.EscapeDataString(source.RabbitUser), new { configure = ".*", write = ".*", read = ".*" });
            permission.EnsureSuccessStatusCode();
            await using var connection = await Broker.Factory(settings).CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            await Broker.DeclareAsync(channel, settings, CancellationToken.None);
            return lab;
        }
        catch { await lab.DisposeAsync(); throw; }
    }

    public Task<OrderTestHost> Orders(Uri payments) => OrderTestHost.Start(OrdersApplication.Build(Arguments("Services:PaymentsBaseUrl", payments)));
    public Task<OrderTestHost> Checkout(Uri orders) => OrderTestHost.Start(CheckoutApplication.Build(Arguments("Services:OrdersBaseUrl", orders)));
    private string[] Arguments(string endpoint, Uri address) =>
    [
        "--urls", "http://127.0.0.1:0", "--Lab:Name", Settings.Name, "--Lab:Root", Settings.Root,
        "--Lab:RabbitHost", Settings.RabbitHost, "--Lab:RabbitPort", Settings.RabbitPort.ToString(),
        "--Lab:RabbitUser", Settings.RabbitUser, "--Lab:RabbitPassword", Settings.RabbitPassword,
        "--" + endpoint, address.ToString()
    ];

    public async Task<OrderSnapshot> Snapshot(OrderTestHost checkout, Guid order, Func<OrderSnapshot, bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (true)
        {
            using var response = await checkout.Client.GetAsync($"/api/v1/orders/{order}", deadline.Token);
            if (response.IsSuccessStatusCode)
            {
                var snapshot = WireJson.Read<OrderSnapshot>(await response.Content.ReadAsStringAsync(deadline.Token));
                if (predicate(snapshot)) return snapshot;
            }
            else if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable)) response.EnsureSuccessStatusCode();
            await Task.Delay(50, deadline.Token);
        }
    }

    public async Task Count(string service, string query, long expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (Convert.ToInt64(OrderTestData.Query(Settings, service, query)) != expected) await Task.Delay(50, deadline.Token);
    }

    public static async Task<BasicGetResult> Receive(IChannel channel, string queue)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var received = await channel.BasicGetAsync(queue, autoAck: true, deadline.Token);
            if (received is not null) return received;
            await Task.Delay(50, deadline.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (created)
            {
                using var deleted = await management.DeleteAsync("/api/vhosts/" + Settings.Name);
                if (deleted.StatusCode != HttpStatusCode.NotFound) deleted.EnsureSuccessStatusCode();
            }
        }
        finally { management.Dispose(); OrderTestData.Clean(Settings); }
    }
}
