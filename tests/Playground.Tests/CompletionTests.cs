using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Orders.Service;
using Payments.Api;
using Playground.Contracts;
using Playground.Infrastructure;
using RabbitMQ.Client;
using Xunit;

namespace Playground.Tests;

public sealed class CompletionTests
{
    [Fact]
    public async Task Concurrent_duplicates_complete_once_and_HTTP_snapshot_exposes_all_identifiers()
    {
        await using var data = await CompletionData.Create();
        var shipment = await data.Seed();
        var processor = data.Processor();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(index => processor.ProcessAsync(index % 2 == 0 ? shipment : shipment with { MessageId = Guid.NewGuid() }, CancellationToken.None)));
        Assert.All(results, result => Assert.True(result));
        var snapshot = data.Store.Get(shipment.OrderId)!;
        var paid = await data.Payments.Snapshot(shipment.OrderId);
        Assert.Equal(OrderStatus.Completed, snapshot.Status);
        Assert.Equal(PaymentStatus.Invoiced, paid.Status);
        Assert.Equal(shipment.ShipmentId, snapshot.ShipmentId);
        Assert.Equal(paid.CaptureId, snapshot.CaptureId);
        Assert.Equal(paid.InvoiceId, snapshot.InvoiceId);
        Assert.Equal(6, data.Scalar("SELECT COUNT(*) FROM shipment_inbox WHERE processed=1"));
        Assert.Equal(1, data.Scalar("SELECT capture_attempts FROM shipment_work"));
        Assert.Equal(1, data.Scalar("SELECT invoice_attempts FROM shipment_work"));
        // A stale operational outbox error must not invalidate the Completed DTO.
        data.Execute("UPDATE outbox SET last_error='publish_retry_exhausted'");
        await using var orders = await OrderTestHost.Orders(data.Orders.Settings);
        await using var checkout = await OrderTestHost.Checkout(data.Orders.Settings, orders.Client.BaseAddress!);
        Assert.Equal(snapshot, await checkout.Snapshot(shipment.OrderId));
        var reopened = data.Orders.NewOrders();
        Assert.Empty(reopened.PendingShipments());
        Assert.True(await data.Processor(store: reopened).ProcessAsync(shipment, CancellationToken.None));
        Assert.Equal(snapshot, reopened.Get(shipment.OrderId));
        Assert.Equal(1, data.PaymentData.Count("SELECT COUNT(*) FROM payments WHERE capture_id IS NOT NULL AND invoice_id IS NOT NULL"));
    }

    [Fact]
    public async Task Unknown_wrong_state_and_incompatible_confirmations_never_charge_another_order()
    {
        await using var data = await CompletionData.Create();
        var shipment = await data.Seed();
        await Assert.ThrowsAsync<ShipmentRejectedException>(() => data.Processor().ProcessAsync(shipment with { OrderId = Guid.NewGuid() }, CancellationToken.None));
        var created = OrderTestData.Message();
        data.Store.Accept(created);
        await Assert.ThrowsAsync<ShipmentRejectedException>(() => data.Processor().ProcessAsync(shipment with { OrderId = created.OrderId }, CancellationToken.None));
        Assert.True(await data.Processor().ProcessAsync(shipment, CancellationToken.None));
        foreach (var incompatible in new[]
        {
            shipment with { OccurredAt = shipment.OccurredAt.AddSeconds(1) },
            shipment with { MessageId = Guid.NewGuid(), ShipmentId = Guid.NewGuid() },
            shipment with { MessageId = Guid.NewGuid(), WarehouseReference = "different" },
            shipment with { MessageId = Guid.NewGuid(), ShippedAt = shipment.ShippedAt.AddSeconds(1) }
        }) await Assert.ThrowsAsync<ShipmentRejectedException>(() => data.Processor().ProcessAsync(incompatible, CancellationToken.None));
        Assert.Equal(1, data.Scalar("SELECT COUNT(*) FROM shipment_inbox"));
        Assert.Equal(1, data.PaymentData.Count("SELECT COUNT(*) FROM payments WHERE capture_id IS NOT NULL"));
        Assert.Equal(OrderStatus.Created, data.Store.Get(created.OrderId)!.Status);
    }

    [Fact]
    public async Task Wrong_mapping_is_blocked_once_but_a_later_valid_confirmation_can_complete()
    {
        await using var data = await CompletionData.Create();
        var valid = await data.Seed();
        var invalid = valid with { MessageId = Guid.NewGuid(), WarehouseReference = "foreign" };
        data.Mappings[invalid.WarehouseReference] = Guid.NewGuid();
        Assert.False(await data.Processor().ProcessAsync(invalid, CancellationToken.None));
        Assert.Equal(OrderStatus.FulfillmentPending, data.Store.Get(valid.OrderId)!.Status);
        Assert.Equal("shipment_mapping_conflict", data.Store.Get(valid.OrderId)!.LastError);
        Assert.Equal(1, data.Scalar("SELECT lookup_attempts FROM shipment_inbox"));
        Assert.Equal(1, data.Scalar("SELECT blocked FROM shipment_inbox"));
        Assert.Empty(data.Store.PendingShipments());
        Assert.Null((await data.Payments.Snapshot(valid.OrderId)).CaptureId);
        Assert.True(await data.Processor().ProcessAsync(valid, CancellationToken.None));
        Assert.Equal(OrderStatus.Completed, data.Store.Get(valid.OrderId)!.Status);
        Assert.Null(data.Store.Get(valid.OrderId)!.LastError);
        Assert.Equal(1, data.Scalar("SELECT COUNT(*) FROM shipment_inbox WHERE processed=1"));
    }

    [Fact]
    public async Task Transient_mapping_404s_retry_with_the_same_reference_and_persisted_attempts()
    {
        await using var data = await CompletionData.Create();
        var shipment = await data.Seed();
        var calls = 0;
        await using var remote = await OrderTestHost.Stub(app => app.MapGet("/internal/v1/warehouse-requests/{reference}", (string reference) =>
            Interlocked.Increment(ref calls) < 3 ? Results.StatusCode(404) : Results.Json(new WarehouseMappingResponse(reference, shipment.OrderId), WireJson.Options)));
        Assert.True(await data.Processor(mapping: remote.Client.BaseAddress).ProcessAsync(shipment, CancellationToken.None));
        Assert.Equal(3, calls);
        Assert.Equal(3, data.Scalar("SELECT lookup_attempts FROM shipment_inbox"));
        Assert.Equal(OrderStatus.Completed, data.Store.Get(shipment.OrderId)!.Status);
    }

    [Fact]
    public async Task Shipment_binding_rolls_back_with_its_state_and_receipt()
    {
        await using var data = await CompletionData.Create();
        var shipment = await data.Seed();
        data.Store.AcceptShipment(shipment);
        data.Execute("CREATE TRIGGER test_fail BEFORE UPDATE OF status ON orders WHEN NEW.status='Shipped' BEGIN SELECT RAISE(ABORT,'test failure'); END");
        Assert.Throws<SqliteException>(() => data.Store.BindShipment(shipment));
        Assert.Equal(0, data.Scalar("SELECT COUNT(*) FROM shipment_work"));
        Assert.Equal(0, data.Scalar("SELECT bound FROM shipment_inbox"));
        Assert.Equal(OrderStatus.FulfillmentPending, data.Store.Get(shipment.OrderId)!.Status);
        data.Execute("DROP TRIGGER test_fail");
        Assert.True(await data.Processor().ProcessAsync(shipment, CancellationToken.None));
    }

    [Theory]
    [InlineData("Captured")]
    [InlineData("Completed")]
    public async Task Remote_success_before_local_commit_failure_recovers_the_same_result_after_reopen(string failedState)
    {
        await using var data = await CompletionData.Create();
        var shipment = await data.Seed();
        data.Execute($"CREATE TRIGGER test_fail BEFORE UPDATE OF status ON orders WHEN NEW.status='{failedState}' BEGIN SELECT RAISE(ABORT,'test failure'); END");
        await Assert.ThrowsAsync<SqliteException>(() => data.Processor().ProcessAsync(shipment, CancellationToken.None));
        var remote = await data.Payments.Snapshot(shipment.OrderId);
        var local = data.Store.Get(shipment.OrderId)!;
        Assert.Equal(failedState == "Captured" ? OrderStatus.Shipped : OrderStatus.Captured, local.Status);
        Assert.Null(failedState == "Captured" ? local.CaptureId : local.InvoiceId);
        Assert.Equal(0, data.Scalar("SELECT processed FROM shipment_inbox"));
        data.Execute("DROP TRIGGER test_fail");
        var reopened = data.Orders.NewOrders();
        Assert.True(await data.Processor(store: reopened).ProcessAsync(Assert.Single(reopened.PendingShipments()), CancellationToken.None));
        var completed = reopened.Get(shipment.OrderId)!;
        Assert.Equal(remote.CaptureId, completed.CaptureId);
        if (failedState == "Completed") Assert.Equal(remote.InvoiceId, completed.InvoiceId);
        Assert.Equal(OrderStatus.Completed, completed.Status);
        Assert.Equal(1, data.PaymentData.Count("SELECT COUNT(*) FROM payments"));
        Assert.Equal(1, data.Scalar("SELECT processed FROM shipment_inbox"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_after_capture_or_invoice_effect_preserves_budget_until_explicit_recovery(bool invoice)
    {
        await using var data = await CompletionData.Create();
        var shipment = await data.Seed();
        if (invoice) await data.SeedCaptured(shipment);
        await data.ReplacePayments(new("happy-path", 500, 0, shipment.OrderId));
        Assert.False(await data.Processor(timeout: TimeSpan.FromMilliseconds(80)).ProcessAsync(shipment, CancellationToken.None));
        var local = data.Store.Get(shipment.OrderId)!;
        Assert.Equal(invoice ? OrderStatus.Captured : OrderStatus.Shipped, local.Status);
        Assert.Equal(invoice ? "invoice_retry_exhausted" : "capture_retry_exhausted", local.LastError);
        var paid = await data.Payments.Snapshot(shipment.OrderId);
        var reopened = data.Orders.NewOrders();
        Assert.Empty(reopened.PendingShipments());
        var column = invoice ? "invoice" : "capture";
        Assert.Equal(3, data.Scalar($"SELECT {column}_attempts FROM shipment_work"));
        data.Execute($"UPDATE shipment_work SET {column}_attempts=0,{column}_blocked=0");
        await data.ReplacePayments(new("happy-path", 0, 0, null));
        Assert.True(await data.Processor(store: reopened).ProcessAsync(Assert.Single(reopened.PendingShipments()), CancellationToken.None));
        Assert.Equal(paid.CaptureId, reopened.Get(shipment.OrderId)!.CaptureId);
        if (invoice) Assert.Equal(paid.InvoiceId, reopened.Get(shipment.OrderId)!.InvoiceId);
        Assert.Equal(OrderStatus.Completed, reopened.Get(shipment.OrderId)!.Status);
        Assert.Null(reopened.Get(shipment.OrderId)!.LastError);
    }

    [Theory]
    [InlineData(false, 400)]
    [InlineData(false, 409)]
    [InlineData(true, 409)]
    public async Task Permanent_payment_errors_block_only_the_reached_step_without_automatic_retry(bool invoice, int status)
    {
        await using var data = await CompletionData.Create();
        var shipment = await data.Seed();
        if (invoice) await data.SeedCaptured(shipment);
        var calls = 0;
        await using var rejected = await OrderTestHost.Stub(app => app.MapPost("/api/v1/{operation}", () =>
        {
            Interlocked.Increment(ref calls);
            return Results.StatusCode(status);
        }));
        Assert.False(await data.Processor(payments: rejected.Client.BaseAddress).ProcessAsync(shipment, CancellationToken.None));
        Assert.Empty(data.Orders.NewOrders().PendingShipments());
        Assert.False(await data.Processor(payments: rejected.Client.BaseAddress).ProcessAsync(shipment with { MessageId = Guid.NewGuid() }, CancellationToken.None));
        Assert.Equal(1, calls);
        var snapshot = data.Store.Get(shipment.OrderId)!;
        Assert.Equal(invoice ? OrderStatus.Captured : OrderStatus.Shipped, snapshot.Status);
        Assert.Equal((invoice ? "invoice" : "capture") + "_payments_request_rejected", snapshot.LastError);
        Assert.Null(snapshot.InvoiceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incoherent_success_responses_are_blocked_without_claiming_completion(bool invoice)
    {
        await using var data = await CompletionData.Create();
        var shipment = await data.Seed();
        if (invoice) await data.SeedCaptured(shipment);
        var work = invoice ? data.Store.Completion(shipment.OrderId) : null;
        await using var invalid = await OrderTestHost.Stub(app =>
        {
            if (invoice) app.MapPost("/api/v1/invoices", () => Results.Json(new InvoiceResponse(shipment.OrderId, Guid.NewGuid(), Guid.NewGuid(), work!.Total), WireJson.Options));
            else app.MapPost("/api/v1/captures", () => Results.Json(new CapturePaymentResponse(shipment.OrderId, Guid.NewGuid(), Guid.NewGuid(), new(25m, "EUR")), WireJson.Options));
        });
        Assert.False(await data.Processor(payments: invalid.Client.BaseAddress).ProcessAsync(shipment, CancellationToken.None));
        Assert.Empty(data.Store.PendingShipments());
        Assert.Equal((invoice ? "invoice" : "capture") + "_payments_invalid_response", data.Store.Get(shipment.OrderId)!.LastError);
        Assert.Null(data.Store.Get(shipment.OrderId)!.InvoiceId);
    }

    [Fact]
    public void Shipment_wire_metadata_and_strict_UTF8_are_checked()
    {
        var shipment = CompletionData.Shipment(Guid.NewGuid());
        var properties = CompletionData.Properties(shipment);
        var body = Encoding.UTF8.GetBytes(WireJson.Write(shipment));
        Assert.Equal(shipment, ShipmentConsumer.Read(body, properties));
        properties.MessageId = Guid.NewGuid().ToString();
        Assert.Throws<ArgumentException>(() => ShipmentConsumer.Read(body, properties));
        Assert.Throws<DecoderFallbackException>(() => ShipmentConsumer.Read(new byte[] { 0xff }, properties));
    }
}

internal sealed class CompletionData : IAsyncDisposable
{
    public OrderTestData Orders { get; } = new();
    public PaymentData PaymentData { get; } = new();
    public OrdersStore Store { get; private set; } = null!;
    public PaymentHost Payments { get; private set; } = null!;
    public OrderTestHost Mapping { get; private set; } = null!;
    public ConcurrentDictionary<string, Guid> Mappings { get; } = new();
    private readonly List<HttpClient> clients = [];
    public static async Task<CompletionData> Create()
    {
        var data = new CompletionData();
        try
        {
            data.Store = data.Orders.NewOrders();
            data.Payments = await PaymentHost.Start(data.PaymentData);
            data.Mapping = await OrderTestHost.Stub(app => app.MapGet("/internal/v1/warehouse-requests/{reference}", (string reference) =>
                data.Mappings.TryGetValue(reference, out var order) ? Results.Json(new WarehouseMappingResponse(reference, order), WireJson.Options) : Results.StatusCode(404)));
            return data;
        }
        catch { await data.DisposeAsync(); throw; }
    }
    public async Task<ShipmentConfirmation> Seed()
    {
        var source = OrderTestData.Message();
        Assert.True(await OrderTestData.Intake(Store, Payments.Client.BaseAddress!).ProcessAsync(source, CancellationToken.None));
        var shipment = Shipment(source.OrderId);
        Mappings[shipment.WarehouseReference] = shipment.OrderId;
        return shipment;
    }
    public async Task SeedCaptured(ShipmentConfirmation shipment)
    {
        Store.AcceptShipment(shipment);
        Store.BindShipment(shipment);
        var work = Store.Completion(shipment.OrderId);
        Store.RecordCapture(await Payments.Post<CapturePaymentResponse>("captures", new CapturePaymentRequest(work.OrderId, work.AuthorizationId)));
    }
    public async Task ReplacePayments(PaymentSimulation simulation)
    {
        await Payments.DisposeAsync();
        Payments = await PaymentHost.Start(PaymentData, simulation);
    }
    public ShipmentProcessor Processor(OrdersStore? store = null, Uri? payments = null, Uri? mapping = null, TimeSpan? timeout = null)
    {
        var paymentClient = new HttpClient { BaseAddress = payments ?? Payments.Client.BaseAddress!, Timeout = timeout ?? TimeSpan.FromSeconds(5) };
        var mappingClient = new HttpClient { BaseAddress = mapping ?? Mapping.Client.BaseAddress!, Timeout = TimeSpan.FromSeconds(5) };
        clients.AddRange([paymentClient, mappingClient]);
        return new(store ?? Store, new(mappingClient), new(paymentClient), NullLogger<ShipmentProcessor>.Instance);
    }
    public static ShipmentConfirmation Shipment(Guid orderId) => new(1, Guid.NewGuid(), DateTimeOffset.UtcNow, orderId, "w-" + Guid.NewGuid().ToString("N"), Guid.NewGuid(), DateTimeOffset.UtcNow);
    public static BasicProperties Properties(ShipmentConfirmation message) => new() { ContentType = "application/json", Type = "ShipmentConfirmation.v1", Persistent = true, MessageId = message.MessageId.ToString() };
    public long Scalar(string sql) => Orders.Scalar("Orders.Service", sql);
    public void Execute(string sql) => Orders.Execute("Orders.Service", sql);
    public async ValueTask DisposeAsync()
    {
        foreach (var client in clients) client.Dispose();
        if (Mapping is not null) await Mapping.DisposeAsync();
        if (Payments is not null) await Payments.DisposeAsync();
        Orders.Dispose();
        PaymentData.Dispose();
    }
}
