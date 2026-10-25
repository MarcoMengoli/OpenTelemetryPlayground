using System.Text.Json;
using Playground.Contracts;
using Playground.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using Response = Playground.Contracts.WarehouseResponse;

namespace Playground.Tests;

[Collection("Failure scenarios")]
[Trait("Category", "Broker")]
[Trait("Category", "Failures")]
public sealed class FailureScenarioTests(ITestOutputHelper output)
{
    [BrokerFact]
    public async Task Duplicates_quarantine_and_reader_restart_complete_one_business_process()
    {
        await using var lab = await FailureLab.Create(output);
        await lab.StartAll();
        await lab.Stop("WarehouseResponse.Service");
        var order = await lab.Checkout();
        await lab.WaitCount("Warehouse.Simulator", "SELECT COUNT(*) FROM warehouse_shipments WHERE sent=1", 1);
        await lab.WaitCount("Inventory.Service", "SELECT COUNT(*) FROM inventory_reservations", 1);
        Assert.Equal(OrderStatus.FulfillmentPending, (await lab.Snapshot(order.OrderId)).Status);
        Assert.Equal(0, lab.Count("Payments.Api", "SELECT COUNT(*) FROM payments WHERE capture_id IS NOT NULL"));

        foreach (var type in new[] { "ReserveInventory", "FulfillOrder" })
        {
            var message = lab.Message("Orders.Service", type);
            await lab.Publish(message);
            await lab.Publish(message);
            var id = Guid.NewGuid();
            var body = type == "ReserveInventory"
                ? WireJson.Write(WireJson.Read<ReserveInventory>(message.Body) with { MessageId = id })
                : WireJson.Write(WireJson.Read<FulfillOrder>(message.Body) with { MessageId = id });
            await lab.Publish(message with { MessageId = id, Body = body });
        }
        await lab.WaitCount("Inventory.Service", "SELECT COUNT(*) FROM inventory_inbox WHERE processed=1", 2);
        await lab.WaitCount("Fulfillment.Service", "SELECT COUNT(*) FROM fulfillment_inbox", 2);
        var request = lab.Text("Fulfillment.Service", "SELECT payload_json FROM warehouse_file_outbox");
        lab.Put("requests", "replayed-request.json", request);
        await FailureLab.Until(() => Task.FromResult(lab.Files("processed/requests").Length >= 2), "duplicate request archived");
        var response = lab.Text("Warehouse.Simulator", "SELECT response_json FROM warehouse_shipments");
        lab.Put("responses", "renamed-response.json", response);
        lab.Put("responses", "malformed.json", "{");
        lab.Put("responses", "unknown.json", WireJson.Write(new Response(1, "unknown-reference", Guid.NewGuid(), DateTimeOffset.UtcNow)));
        await lab.Start("WarehouseResponse.Service");
        var completed = await lab.Completed(order.OrderId);
        await FailureLab.Until(() => Task.FromResult(lab.Files("errors/responses").Length == 2), "malformed and unknown quarantine");
        var acquired = WireJson.Read<Response>(response);
        Assert.Equal(3, lab.Count("WarehouseResponse.Service", "SELECT attempts FROM responses WHERE reference='unknown-reference'"));
        Assert.Equal("Rejected", lab.Text("WarehouseResponse.Service", "SELECT state FROM responses WHERE reference='unknown-reference'"));
        Assert.Equal(acquired.ShipmentId, completed.ShipmentId);
        Assert.True(lab.Files("processed/responses").Length >= 2);

        var confirmation = lab.Message("WarehouseResponse.Service", "ShipmentConfirmation");
        await lab.Publish(confirmation);
        var duplicate = WireJson.Read<ShipmentConfirmation>(confirmation.Body) with { MessageId = Guid.NewGuid() };
        await lab.Publish(confirmation with { MessageId = duplicate.MessageId, Body = WireJson.Write(duplicate) });
        await lab.WaitCount("Orders.Service", "SELECT COUNT(*) FROM shipment_inbox WHERE processed=1", 2);
        lab.AssertSingleEffects(await lab.Completed(order.OrderId));
        // Files expose exactly the agreed business fields, including archived copies.
        foreach (var file in lab.Files("processed/responses"))
        {
            using var json = JsonDocument.Parse(WarehouseFiles.Read(file));
            Assert.Equal(new[] { "schemaVersion", "shipmentId", "shippedAt", "warehouseReference" },
                json.RootElement.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
        }
    }

    [BrokerFact]
    public async Task Missing_response_remains_pending_until_explicit_simulator_recovery()
    {
        await using var lab = await FailureLab.Create(output);
        lab.Configure("Warehouse.Simulator", "--Simulation:Profile", "missing-warehouse-response");
        await lab.StartAll();
        var order = await lab.Checkout();
        await lab.WaitCount("Warehouse.Simulator", "SELECT COUNT(*) FROM warehouse_shipments WHERE sent=0", 1);
        var original = lab.Text("Warehouse.Simulator", "SELECT response_json FROM warehouse_shipments");
        // A bounded observation interval, not an invented automatic cancellation.
        var until = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < until)
        {
            Assert.Equal(OrderStatus.FulfillmentPending, (await lab.Snapshot(order.OrderId)).Status);
            Assert.Equal(0, lab.Count("Payments.Api", "SELECT COUNT(*) FROM payments WHERE capture_id IS NOT NULL"));
            Assert.Equal(0, lab.Count("WarehouseResponse.Service", "SELECT COUNT(*) FROM outbox"));
            Assert.Empty(lab.Files("responses"));
            await Task.Delay(150);
        }
        await lab.Stop("Warehouse.Simulator", kill: true);
        lab.Configure("Warehouse.Simulator");
        await lab.Start("Warehouse.Simulator");
        var completed = await lab.Completed(order.OrderId);
        Assert.Equal(WireJson.Read<Response>(original).ShipmentId, completed.ShipmentId);
        lab.AssertSingleEffects(completed);
    }

    [BrokerFact]
    public async Task Temporary_payment_and_courier_errors_recover_with_persisted_attempts()
    {
        await using var lab = await FailureLab.Create(output);
        lab.Configure("Payments.Api", "--Simulation:Profile", "temporary-payment-failure", "--Simulation:FailuresBeforeSuccess", "2");
        lab.Configure("Couriers.Api", "--Simulation:Profile", "temporary-courier-failure", "--Simulation:FailuresBeforeSuccess", "2");
        await lab.StartAll();
        var order = await lab.Checkout();
        lab.AssertSingleEffects(await lab.Completed(order.OrderId));
        Assert.Equal(3, lab.Count("Orders.Service", "SELECT auth_attempts FROM orders"));
        Assert.Equal(3, lab.Count("Orders.Service", "SELECT capture_attempts FROM shipment_work"));
        Assert.Equal(3, lab.Count("Orders.Service", "SELECT invoice_attempts FROM shipment_work"));
        Assert.Equal(3, lab.Count("Payments.Api", "SELECT COUNT(*) FROM payment_attempts WHERE attempts=3"));
        Assert.Equal(3, lab.Count("Couriers.Api", "SELECT attempts FROM courier_estimates"));
        Assert.Equal(3, lab.Count("Fulfillment.Service", "SELECT attempts FROM fulfillment_work"));
    }

    [BrokerFact]
    public Task Slow_payment_exhaustion_preserves_remote_authorization_and_recovers_explicitly() => SlowAndRecover(payment: true);

    [BrokerFact]
    public Task Slow_courier_exhaustion_preserves_estimate_and_recovers_explicitly() => SlowAndRecover(payment: false);

    private async Task SlowAndRecover(bool payment)
    {
        await using var lab = await FailureLab.Create(output);
        var server = payment ? "Payments.Api" : "Couriers.Api";
        var consumer = payment ? "Orders.Service" : "Fulfillment.Service";
        lab.Configure(server, "--Simulation:Profile", payment ? "happy-path" : "slow-courier", "--Simulation:DelayMs", "6000");
        await lab.StartAll();
        var order = await lab.Checkout();
        var source = payment ? lab.Message("Checkout.Api", "OrderCreated") : await FulfillmentCommand();
        Assert.Equal(source.MessageId.ToString(), await lab.ErrorMessage(payment ? "orders.order-created.v1" : "fulfillment.fulfill.v1"));
        var remote = payment ? lab.Text("Payments.Api", "SELECT authorization_id FROM payments")
            : lab.Text("Couriers.Api", "SELECT response_json FROM courier_estimates");
        Assert.Equal(payment ? OrderStatus.Created : OrderStatus.FulfillmentPending, (await lab.Snapshot(order.OrderId)).Status);
        Assert.Equal(0, lab.Count("Payments.Api", "SELECT COUNT(*) FROM payments WHERE capture_id IS NOT NULL"));
        Assert.Equal(3, lab.Count(consumer, payment ? "SELECT auth_attempts FROM orders" : "SELECT attempts FROM fulfillment_work"));
        Assert.Equal(payment ? "payment_retry_exhausted" : "courier_timeout",
            lab.Text(consumer, payment ? "SELECT last_error FROM orders" : "SELECT last_error FROM fulfillment_work"));
        await lab.Stop(consumer, kill: true);
        await lab.Start(consumer);
        await Task.Delay(1000);
        Assert.Equal(3, lab.Count(consumer, payment ? "SELECT auth_attempts FROM orders" : "SELECT attempts FROM fulfillment_work"));
        await lab.Stop(consumer);
        await lab.Stop(server);
        lab.Configure(server);
        await lab.Start(server);
        lab.Query(consumer, payment
            ? "UPDATE orders SET auth_attempts=0,authorization_blocked=0 WHERE order_id=$id AND status='Created'"
            : "UPDATE fulfillment_work SET attempts=0,state='Pending',last_error=NULL WHERE order_id=$id AND state='Failed'",
            ("$id", order.OrderId.ToString()));
        await lab.Start(consumer);
        var completed = await lab.Completed(order.OrderId);
        if (payment) Assert.Equal(remote, completed.AuthorizationId!.Value.ToString());
        else Assert.Equal(remote, lab.Text("Couriers.Api", "SELECT response_json FROM courier_estimates"));
        lab.AssertSingleEffects(completed);
        // Historical DLQ evidence remains; no automatic resend/new business ID.
        Assert.Equal(source.MessageId.ToString(), await lab.ErrorMessage(payment ? "orders.order-created.v1" : "fulfillment.fulfill.v1"));

        async Task<OutboxMessage> FulfillmentCommand()
        {
            await lab.WaitCount("Orders.Service", "SELECT COUNT(*) FROM outbox WHERE type='FulfillOrder.v1'", 1);
            return lab.Message("Orders.Service", "FulfillOrder");
        }
    }

    [BrokerFact]
    public Task Kill_orders_after_remote_capture_before_local_commit_recovers_same_capture() => KillAtPaymentBoundary(true);

    [BrokerFact]
    public Task Kill_orders_after_capture_commit_before_invoice_recovers_remaining_work() => KillAtPaymentBoundary(false);

    private async Task KillAtPaymentBoundary(bool afterRemoteCapture)
    {
        await using var lab = await FailureLab.Create(output);
        await lab.Start("Payments.Api");
        await using var boundary = await PaymentBoundary.Start(lab.Url("Payments.Api"), afterRemoteCapture);
        lab.PaymentsProxy = boundary.Address;
        await lab.StartAll();
        var order = await lab.Checkout();
        await boundary.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var capture = lab.Text("Payments.Api", "SELECT capture_id FROM payments");
        Assert.Equal(0, lab.Count("Payments.Api", "SELECT COUNT(*) FROM payments WHERE invoice_id IS NOT NULL"));
        Assert.Equal(afterRemoteCapture ? "Shipped" : "Captured", lab.Text("Orders.Service", "SELECT status FROM orders"));
        Assert.Equal(0, lab.Count("Orders.Service", "SELECT processed FROM shipment_inbox"));
        await lab.Stop("Orders.Service", kill: true);
        lab.PaymentsProxy = null;
        await lab.Start("Orders.Service");
        var completed = await lab.Completed(order.OrderId);
        Assert.Equal(capture, completed.CaptureId!.Value.ToString());
        lab.AssertSingleEffects(completed);
    }

    [BrokerFact]
    public async Task Broker_stop_keeps_confirmation_durable_and_full_lab_restart_recovers_it()
    {
        await using var lab = await FailureLab.Create(output);
        lab.Configure("Warehouse.Simulator", "--Simulation:Profile", "missing-warehouse-response");
        await lab.StartAll();
        var order = await lab.Checkout();
        await lab.WaitCount("Warehouse.Simulator", "SELECT COUNT(*) FROM warehouse_shipments WHERE sent=0", 1);
        var response = lab.Text("Warehouse.Simulator", "SELECT response_json FROM warehouse_shipments");
        await lab.StopBroker();
        // Release exactly the real simulator's durable business result while AMQP is unavailable.
        lab.Put("responses", "released-response.json", response);
        await lab.WaitCount("WarehouseResponse.Service", "SELECT COUNT(*) FROM outbox WHERE sent=0 AND attempts=3 AND last_error='publish_retry_exhausted'", 1);
        var pending = lab.Message("WarehouseResponse.Service", "ShipmentConfirmation");
        Assert.Equal(0, lab.Count("Payments.Api", "SELECT COUNT(*) FROM payments WHERE capture_id IS NOT NULL"));
        await lab.StopAll();
        await lab.StartBroker();
        lab.Query("WarehouseResponse.Service", "UPDATE outbox SET attempts=0,last_error=NULL WHERE message_id=$id AND sent=0", ("$id", pending.MessageId.ToString()));
        lab.Configure("Warehouse.Simulator");
        await lab.StartAll();
        lab.AssertSingleEffects(await lab.Completed(order.OrderId));
        Assert.Equal(pending, lab.Message("WarehouseResponse.Service", "ShipmentConfirmation"));
        Assert.Equal(1, lab.Count("WarehouseResponse.Service", "SELECT COUNT(*) FROM outbox WHERE sent=1"));
    }
}
