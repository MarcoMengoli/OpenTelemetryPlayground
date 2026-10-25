using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Orders.Service;
using Playground.Contracts;
using Playground.Infrastructure;
using RabbitMQ.Client;
using Xunit;

namespace Playground.Tests;

[Trait("Category", "Broker")]
public sealed class CompletionBrokerTests
{
    [BrokerFact]
    public async Task Real_consumer_completes_once_for_transport_and_logical_duplicates_including_restart()
    {
        await using var lab = await CompletionBrokerLab.Create();
        await using var data = await CompletionData.Create();
        var message = await lab.Seed(data);
        await using (var orders = await lab.Start(data))
        {
            await lab.Publish(message);
            await lab.Publish(message);
            await lab.Publish(message with { MessageId = Guid.NewGuid() });
            await lab.Idle(3);
            var snapshot = await orders.Snapshot(message.OrderId, internalRoute: true);
            var payment = await data.Payments.Snapshot(message.OrderId);
            Assert.Equal(OrderStatus.Completed, snapshot.Status);
            Assert.Equal(payment.CaptureId, snapshot.CaptureId);
            Assert.Equal(payment.InvoiceId, snapshot.InvoiceId);
            Assert.Equal(message.ShipmentId, snapshot.ShipmentId);
        }
        await using var restarted = await lab.Start(data);
        await lab.Publish(message with { MessageId = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow });
        await lab.Idle(4);
        Assert.Equal(1, lab.Scalar("SELECT capture_attempts FROM shipment_work"));
        Assert.Equal(1, lab.Scalar("SELECT invoice_attempts FROM shipment_work"));
        Assert.Equal(3, lab.Scalar("SELECT COUNT(*) FROM shipment_inbox WHERE processed=1"));
        Assert.Equal(1, data.PaymentData.Count("SELECT COUNT(*) FROM payments WHERE invoice_id IS NOT NULL"));
    }

    [BrokerFact]
    public async Task Unknown_invalid_and_conflicting_confirmations_dead_letter_without_extra_payments()
    {
        await using var lab = await CompletionBrokerLab.Create();
        await using var data = await CompletionData.Create();
        var valid = await lab.Seed(data);
        await using var orders = await lab.Start(data);
        await lab.Publish(valid with { OrderId = Guid.NewGuid() });
        await using var connection = await Broker.Factory(lab.Settings).CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        await channel.BasicPublishAsync(lab.Settings.Exchange, "shipment.confirmed.v1", true, CompletionData.Properties(valid), Encoding.UTF8.GetBytes("{}"));
        var wrongProperties = CompletionData.Properties(valid);
        wrongProperties.MessageId = Guid.NewGuid().ToString();
        await channel.BasicPublishAsync(lab.Settings.Exchange, "shipment.confirmed.v1", true, wrongProperties, Encoding.UTF8.GetBytes(WireJson.Write(valid)));
        for (var index = 0; index < 3; index++) _ = await OrderBrokerLab.Receive(channel, lab.Queue + ".errors");
        Assert.Null((await data.Payments.Snapshot(valid.OrderId)).CaptureId);
        await lab.Publish(valid);
        await lab.Idle(1);
        await lab.Publish(valid with { MessageId = Guid.NewGuid(), ShipmentId = Guid.NewGuid() });
        _ = await OrderBrokerLab.Receive(channel, lab.Queue + ".errors");
        Assert.Equal(1, lab.Scalar("SELECT COUNT(*) FROM shipment_inbox"));
        Assert.Equal(1, data.PaymentData.Count("SELECT COUNT(*) FROM payments WHERE capture_id IS NOT NULL AND invoice_id IS NOT NULL"));
        Assert.Equal(OrderStatus.Completed, (await orders.Snapshot(valid.OrderId, true)).Status);
    }

    [BrokerFact]
    public Task Stop_after_remote_capture_before_local_capture_commit_recovers_original_capture() => StopAndRecover(beforeLocalCapture: true);

    [BrokerFact]
    public Task Stop_after_local_capture_before_invoice_recovers_only_the_remaining_step() => StopAndRecover(beforeLocalCapture: false);

    private static async Task StopAndRecover(bool beforeLocalCapture)
    {
        await using var lab = await CompletionBrokerLab.Create();
        await using var data = await CompletionData.Create();
        var message = await lab.Seed(data);
        var boundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var paused = await OrderTestHost.Stub(app =>
        {
            app.MapPost("/api/v1/captures", async (CapturePaymentRequest request, CancellationToken token) =>
            {
                var response = await data.Payments.Post<CapturePaymentResponse>("captures", request);
                if (beforeLocalCapture)
                {
                    boundary.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
                return Results.Json(response, WireJson.Options);
            });
            app.MapPost("/api/v1/invoices", async (CancellationToken token) =>
            {
                boundary.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return Results.StatusCode(503);
            });
        });
        PaymentSnapshot paid;
        await using (var first = await lab.Start(data, paused.Client.BaseAddress))
        {
            await lab.Publish(message);
            await boundary.Task.WaitAsync(TimeSpan.FromSeconds(15));
            paid = await data.Payments.Snapshot(message.OrderId);
            Assert.Equal(PaymentStatus.Captured, paid.Status);
            Assert.Null(paid.InvoiceId);
            var snapshot = await first.Snapshot(message.OrderId, true);
            Assert.Equal(beforeLocalCapture ? OrderStatus.Shipped : OrderStatus.Captured, snapshot.Status);
            Assert.Equal(0, lab.Scalar("SELECT processed FROM shipment_inbox"));
            await first.App.StopAsync();
        }
        await using var restarted = await lab.Start(data);
        await lab.Idle(1);
        var completed = await restarted.Snapshot(message.OrderId, true);
        Assert.Equal(OrderStatus.Completed, completed.Status);
        Assert.Equal(paid.CaptureId, completed.CaptureId);
        Assert.NotNull(completed.InvoiceId);
        Assert.Equal(beforeLocalCapture ? 2 : 1, lab.Scalar("SELECT capture_attempts FROM shipment_work"));
        Assert.Equal(beforeLocalCapture ? 1 : 2, lab.Scalar("SELECT invoice_attempts FROM shipment_work"));
        Assert.Equal(1, data.PaymentData.Count("SELECT COUNT(*) FROM payments"));
    }

    [BrokerFact]
    public async Task Real_five_second_capture_timeouts_exhaust_dead_letter_and_recover_explicitly()
    {
        await using var lab = await CompletionBrokerLab.Create();
        await using var data = await CompletionData.Create();
        var message = await lab.Seed(data);
        await data.ReplacePayments(new("happy-path", 6000, 0, message.OrderId));
        await using var connection = await Broker.Factory(lab.Settings).CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await using (var first = await lab.Start(data))
        {
            await lab.Publish(message);
            var rejected = await OrderBrokerLab.Receive(channel, lab.Queue + ".errors");
            Assert.Equal(message.MessageId.ToString(), rejected.BasicProperties.MessageId);
            var snapshot = await first.Snapshot(message.OrderId, true);
            Assert.Equal(OrderStatus.Shipped, snapshot.Status);
            Assert.Equal("capture_retry_exhausted", snapshot.LastError);
            Assert.Null(snapshot.CaptureId);
        }
        var paid = await data.Payments.Snapshot(message.OrderId);
        Assert.Equal(PaymentStatus.Captured, paid.Status);
        Assert.Equal(3, lab.Scalar("SELECT capture_attempts FROM shipment_work"));
        var store = new OrdersStore(lab.Settings);
        store.Initialize();
        Assert.Empty(store.PendingShipments());
        lab.Query("UPDATE shipment_work SET capture_attempts=0 WHERE order_id='" + message.OrderId + "'");
        await data.ReplacePayments(new("happy-path", 0, 0, null));
        await using var recovered = await lab.Start(data);
        await lab.Wait("SELECT processed FROM shipment_inbox", 1);
        // The original is already in the error queue; an operator resend keeps its identity.
        await lab.Publish(message);
        await lab.Idle(1);
        var completed = await recovered.Snapshot(message.OrderId, true);
        Assert.Equal(OrderStatus.Completed, completed.Status);
        Assert.Equal(paid.CaptureId, completed.CaptureId);
        Assert.Equal(1, data.PaymentData.Count("SELECT COUNT(*) FROM payments"));
    }
}

internal sealed class CompletionBrokerLab(LabSettings settings, HttpClient management) : IAsyncDisposable
{
    public LabSettings Settings { get; } = settings;
    public string Queue => Settings.Name + ".orders.shipment-confirmation.v1";
    private bool created;
    public static async Task<CompletionBrokerLab> Create()
    {
        var source = LabSettings.Read(new ConfigurationBuilder().AddEnvironmentVariables().Build());
        var name = "us06-" + Guid.NewGuid().ToString("N")[..20];
        var settings = source with { Name = name, Root = Path.Combine(Path.GetTempPath(), "playground-" + name) };
        var management = new HttpClient { BaseAddress = new Uri($"http://{source.RabbitHost}:{source.RabbitPort + 1}"), Timeout = TimeSpan.FromSeconds(10) };
        management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(source.RabbitUser + ":" + source.RabbitPassword)));
        var lab = new CompletionBrokerLab(settings, management);
        try
        {
            using var result = await management.PutAsJsonAsync("/api/vhosts/" + name, new { });
            result.EnsureSuccessStatusCode();
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
    public async Task<ShipmentConfirmation> Seed(CompletionData data)
    {
        var store = new OrdersStore(Settings);
        store.Initialize();
        var source = OrderTestData.Message();
        Assert.True(await OrderTestData.Intake(store, data.Payments.Client.BaseAddress!).ProcessAsync(source, CancellationToken.None));
        var shipment = CompletionData.Shipment(source.OrderId);
        data.Mappings[shipment.WarehouseReference] = shipment.OrderId;
        return shipment;
    }
    public Task<OrderTestHost> Start(CompletionData data, Uri? payments = null) => OrderTestHost.Start(OrdersApplication.Build(
    [
        "--urls", "http://127.0.0.1:0", "--Lab:Name", Settings.Name, "--Lab:Root", Settings.Root,
        "--Lab:RabbitHost", Settings.RabbitHost, "--Lab:RabbitPort", Settings.RabbitPort.ToString(),
        "--Lab:RabbitUser", Settings.RabbitUser, "--Lab:RabbitPassword", Settings.RabbitPassword,
        "--Services:PaymentsBaseUrl", (payments ?? data.Payments.Client.BaseAddress!).ToString(),
        "--Services:FulfillmentBaseUrl", data.Mapping.Client.BaseAddress!.ToString()
    ]));
    public async Task Publish(ShipmentConfirmation message)
    {
        await using var publisher = new ConfirmedPublisher(Settings);
        await publisher.PublishAsync(new(message.MessageId, message.OrderId, "ShipmentConfirmation.v1", "shipment.confirmed.v1", WireJson.Write(message)), CancellationToken.None);
    }
    public object? Query(string sql) => OrderTestData.Query(Settings, "Orders.Service", sql);
    public long Scalar(string sql) => Convert.ToInt64(Query(sql));
    public async Task Wait(string sql, long expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (Scalar(sql) != expected) await Task.Delay(50, deadline.Token);
    }
    public async Task Idle(long acknowledgements)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            using var response = await management.GetAsync("/api/queues/" + Settings.Name + "/" + Queue, deadline.Token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            var root = json.RootElement;
            if (root.TryGetProperty("messages", out var count) && count.GetInt64() == 0 &&
                root.TryGetProperty("message_stats", out var stats) && stats.TryGetProperty("ack", out var ack) && ack.GetInt64() >= acknowledgements) return;
            await Task.Delay(100, deadline.Token);
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
