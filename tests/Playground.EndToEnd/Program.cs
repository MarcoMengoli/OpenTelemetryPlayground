using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Playground.Contracts;
using Playground.Infrastructure;

try
{
    var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args).Build();
    if (configuration["E2E:InventoryOrderId"] is { } inventoryOrder) {
        await InventoryInspection.PrintAsync(configuration, Guid.Parse(inventoryOrder));
        return 0;
    }
    await using var verification = new EcommerceVerification(configuration);
    await verification.RunAsync();
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"End-to-end verification FAILED: {exception.Message}");
    return 1;
}

sealed class EcommerceVerification : IAsyncDisposable
{
    private static readonly string[] Services = ["Checkout.Api", "Orders.Service", "Payments.Api", "Inventory.Service", "Fulfillment.Service", "Couriers.Api", "WarehouseResponse.Service", "Warehouse.Simulator"];
    private readonly LabSettings settings;
    private readonly Guid declinedId;
    private readonly Guid orderId = Guid.NewGuid();
    private readonly string reportPath;
    private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(180));
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly HttpClient management = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<Guid, OrderSnapshot> snapshots = new();
    private readonly List<string> observed = [];
    private string phase = "prerequisites";
    private string? reference;
    private PaymentSnapshot? payment;
    private string? error;
    private bool succeeded;

    public EcommerceVerification(IConfiguration configuration)
    {
        if (configuration["Lab:Root"] is null || configuration["Lab:Name"] is null || configuration["Lab:RabbitPort"] is null)
            throw new ArgumentException("Explicit Lab:Name, Lab:Root and Lab:RabbitPort are required; use scripts/e2e.ps1.");
        settings = LabSettings.Read(configuration);
        if (!Guid.TryParse(configuration["E2E:DeclinedOrderId"], out declinedId) || declinedId == Guid.Empty)
            throw new ArgumentException("E2E:DeclinedOrderId must match the selected Payments simulation order.");
        reportPath = Path.GetFullPath(configuration["E2E:Report"] ?? throw new ArgumentException("E2E:Report is required."));
        management.BaseAddress = new Uri($"http://{settings.RabbitHost}:{settings.RabbitPort + 1}");
        management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.RabbitUser + ":" + settings.RabbitPassword)));
    }

    public async Task RunAsync()
    {
        try
        {
            SetPhase("readiness and empty environment");
            await Readiness();
            foreach (var (service, table) in new[]
            {
                ("Checkout.Api", "checkouts"), ("Orders.Service", "orders"), ("Payments.Api", "payments"),
                ("Inventory.Service", "inventory_reservations"), ("Inventory.Service", "inventory_stock"),
                ("Fulfillment.Service", "fulfillment_work"), ("Couriers.Api", "courier_estimates"),
                ("Warehouse.Simulator", "warehouse_shipments"), ("WarehouseResponse.Service", "responses")
            }) Require(Count(service, table) == 0, $"Environment is not empty: {service}/{table}.");
            Require(!Directory.EnumerateFiles(settings.Warehouse, "*.json", SearchOption.AllDirectories).Any(), "Warehouse contains files from another run.");

            var accepted = Request(orderId);
            var declined = Request(declinedId);
            SetPhase("checkout acceptance and identical retries");
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Checkout(accepted)));
            await Checkout(declined);
            await Checkout(declined);

            SetPhase("normal order reaches Completed and declined order reaches PaymentDeclined");
            await Until(async () => await Reached(orderId, OrderStatus.Completed) & await Reached(declinedId, OrderStatus.PaymentDeclined));

            SetPhase("Inventory reserved independently of order completion");
            await Until(() => Task.FromResult(Count("Inventory.Service", "inventory_reservations", orderId) == accepted.Items.Length));
            foreach (var item in accepted.Items)
            {
                Require(Number("Inventory.Service", "SELECT quantity FROM inventory_reservations WHERE order_id=$order AND sku=$sku", ("$order", orderId.ToString()), ("$sku", item.Sku)) == item.Quantity, "Wrong reserved quantity for " + item.Sku);
                Require(Number("Inventory.Service", "SELECT reserved FROM inventory_stock WHERE sku=$sku", ("$sku", item.Sku)) == item.Quantity, "Wrong aggregate reserved for " + item.Sku);
            }

            SetPhase("warehouse files, outboxes and broker settle");
            reference = (string?)Query("Fulfillment.Service", "SELECT warehouse_reference FROM fulfillment_work WHERE order_id=$order", ("$order", orderId.ToString()));
            Require(reference is not null, "Missing warehouse mapping.");
            await Until(async () => OutboxesSent() && Archived("requests").Length > 0 && Archived("responses").Length > 0 &&
                !Directory.EnumerateFiles(Path.Combine(settings.Warehouse, "requests"), "*.json").Any() &&
                !Directory.EnumerateFiles(Path.Combine(settings.Warehouse, "responses"), "*.json").Any() && await QueuesDrained());

            SetPhase("reconcile real business effects and references");
            await VerifyEffects(accepted);
            succeeded = true;
            SetPhase("completed");
            Console.WriteLine($"Completed {orderId}: shipment {snapshots[orderId].ShipmentId}, capture {payment!.CaptureId}, invoice {payment.InvoiceId}; declined {declinedId}; Inventory and warehouse reconciled.");
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            throw new InvalidOperationException($"Phase '{phase}' in {settings.Name}: {exception.Message}. See {reportPath} and host logs.", exception);
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var report = new { succeeded, environment = settings.Name, root = settings.Root, portBase = settings.RabbitPort, phase, orderId, declinedOrderId = declinedId,
                warehouseReference = reference, snapshots = snapshots.Values, payment, observedStates = observed, error, finishedAt = DateTimeOffset.UtcNow };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions(WireJson.Options) { WriteIndented = true }));
        }
    }

    private static CheckoutRequest Request(Guid id) => new(id, [new("BOOK", 2, 12.50m), new("PEN", 3, 1.50m)], new("Ada", "Via Roma 1", "Bologna", "40100", "IT"));
    private string Url(int offset, string path) => $"http://127.0.0.1:{settings.RabbitPort + offset}{path}";
    private async Task Readiness()
    {
        foreach (var (service, index) in Services.Select((service, index) => (service, index)))
        {
            using var response = await http.GetAsync(Url(10 + index, "/health/ready"), deadline.Token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            Require(json.RootElement.GetProperty("service").GetString() == service && json.RootElement.GetProperty("status").GetString() == "ready", "Unexpected service/readiness for " + service);
        }
    }
    private async Task Checkout(CheckoutRequest input)
    {
        using var response = await http.PostAsJsonAsync(Url(10, "/api/v1/orders"), input, WireJson.Options, deadline.Token);
        Require(response.StatusCode == HttpStatusCode.Accepted, $"Checkout {input.OrderId}: HTTP {(int)response.StatusCode}, {await response.Content.ReadAsStringAsync(deadline.Token)}");
        Require(response.Headers.Location?.ToString() == "/api/v1/orders/" + input.OrderId, "Wrong checkout Location.");
        var accepted = WireJson.Read<OrderAccepted>(await response.Content.ReadAsStringAsync(deadline.Token));
        Require(accepted.OrderId == input.OrderId, "Checkout returned a different OrderId.");
    }
    private async Task<bool> Reached(Guid id, OrderStatus expected)
    {
        using var response = await http.GetAsync(Url(10, "/api/v1/orders/" + id), deadline.Token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable) return false;
        response.EnsureSuccessStatusCode();
        var snapshot = WireJson.Read<OrderSnapshot>(await response.Content.ReadAsStringAsync(deadline.Token));
        Require(snapshot.OrderId == id, "Snapshot returned a different order.");
        if (!snapshots.TryGetValue(id, out var previous) || previous.Status != snapshot.Status)
        {
            observed.Add($"{id}: {snapshot.Status}");
            Console.WriteLine($"Order {id}: {snapshot.Status}");
        }
        snapshots[id] = snapshot;
        if (snapshot.Status is OrderStatus.Completed or OrderStatus.PaymentDeclined)
            Require(snapshot.Status == expected, $"Unexpected terminal state for {id}: {snapshot.Status}.");
        return snapshot.Status == expected;
    }
    private async Task Until(Func<Task<bool>> predicate)
    {
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (await predicate()) return;
            await Task.Delay(200, deadline.Token);
        }
    }
    private async Task VerifyEffects(CheckoutRequest request)
    {
        var complete = snapshots[orderId];
        var declined = snapshots[declinedId];
        payment = await Payment(orderId);
        var refused = await Payment(declinedId);
        Require(payment.OrderId == orderId && payment.Status == PaymentStatus.Invoiced && complete.AuthorizationId == payment.AuthorizationId && complete.CaptureId == payment.CaptureId && complete.InvoiceId == payment.InvoiceId, "Payment/order identities differ.");
        Require(payment.Total == OrderContent.Total(request.Items) && payment.InvoiceId.HasValue && complete.LastError is null, "Incomplete or inconsistent normal payment.");
        Require(refused.OrderId == declinedId && refused.Status == PaymentStatus.Declined && refused.AuthorizationId is null && refused.CaptureId is null && refused.InvoiceId is null &&
            declined.ShipmentId is null && declined.CaptureId is null && declined.InvoiceId is null && declined.FailureCode == "simulated_decline", "Decline has unintended effects.");
        Require(Count("Checkout.Api", "checkouts") == 2 && Count("Orders.Service", "orders") == 2 && Count("Payments.Api", "payments") == 2, "Unexpected business order/payment count.");
        Require(Number("Payments.Api", "SELECT COUNT(*) FROM payments WHERE capture_id IS NOT NULL") == 1 && Number("Payments.Api", "SELECT COUNT(*) FROM payments WHERE invoice_id IS NOT NULL") == 1, "Duplicate capture/invoice effects.");
        Require(Count("Inventory.Service", "inventory_reservations") == request.Items.Length && Count("Inventory.Service", "inventory_stock") == request.Items.Length, "Unexpected Inventory effects.");
        foreach (var (service, table) in new[] { ("Inventory.Service", "inventory_reservations"), ("Fulfillment.Service", "fulfillment_work"), ("Couriers.Api", "courier_estimates"), ("Orders.Service", "outbox") })
            Require(Count(service, table, declinedId) == 0, "Declined order reached " + service + "/" + table);
        foreach (var (service, table) in new[] { ("Fulfillment.Service", "fulfillment_work"), ("Couriers.Api", "courier_estimates"), ("Warehouse.Simulator", "warehouse_shipments"), ("WarehouseResponse.Service", "responses") })
            Require(Count(service, table) == 1, "Unexpected fulfillment/shipment count in " + service);
        Require(Count("Orders.Service", "outbox", orderId) == 2 && Count("Checkout.Api", "outbox") == 2 && Count("WarehouseResponse.Service", "outbox") == 1, "Wrong number of downstream publications.");

        using var lookup = await http.GetAsync(Url(14, "/internal/v1/warehouse-requests/" + Uri.EscapeDataString(reference!)), deadline.Token);
        lookup.EnsureSuccessStatusCode();
        var mapping = WireJson.Read<WarehouseMappingResponse>(await lookup.Content.ReadAsStringAsync(deadline.Token));
        Require(mapping.OrderId == orderId && mapping.WarehouseReference == reference, "HTTP warehouse mapping mismatch.");
        var simulator = WireJson.Read<WarehouseResponse>((string)Query("Warehouse.Simulator", "SELECT response_json FROM warehouse_shipments WHERE reference=$ref", ("$ref", reference!))!);
        var received = WireJson.Read<WarehouseResponse>((string)Query("WarehouseResponse.Service", "SELECT response_json FROM responses WHERE reference=$ref AND state='Ready' AND order_id=$order", ("$ref", reference!), ("$order", orderId.ToString()))!);
        Require(simulator == received && simulator.ShipmentId == complete.ShipmentId, "Warehouse shipment identities differ.");
        var confirmation = WireJson.Read<ShipmentConfirmation>((string)Query("WarehouseResponse.Service", "SELECT body FROM outbox WHERE order_id=$order", ("$order", orderId.ToString()))!);
        Require(confirmation.OrderId == orderId && confirmation.WarehouseReference == reference && confirmation.ShipmentId == simulator.ShipmentId && confirmation.ShippedAt == simulator.ShippedAt, "AMQP warehouse confirmation mismatch.");
        var estimate = WireJson.Read<DeliveryEstimateResponse>((string)Query("Couriers.Api", "SELECT response_json FROM courier_estimates WHERE order_id=$order", ("$order", orderId.ToString()))!);
        foreach (var file in Archived("requests"))
        {
            var content = WireJson.Read<WarehouseRequest>(WarehouseFiles.Read(file));
            Require(content.WarehouseReference == reference && content.Items.SequenceEqual(request.Items) && content.ShippingAddress == request.ShippingAddress && content.CourierCode == estimate.CourierCode && content.EstimatedDays == estimate.EstimatedDays, "Archived warehouse request mismatch: " + file);
        }
        foreach (var file in Archived("responses")) Require(WireJson.Read<WarehouseResponse>(WarehouseFiles.Read(file)) == simulator, "Archived warehouse response mismatch: " + file);
        Require(!Directory.EnumerateFiles(Path.Combine(settings.Warehouse, "errors"), "*.json", SearchOption.AllDirectories).Any(), "Warehouse has quarantined files.");
    }
    private async Task<PaymentSnapshot> Payment(Guid id)
    {
        using var response = await http.GetAsync(Url(12, "/api/v1/payments/" + id), deadline.Token);
        response.EnsureSuccessStatusCode();
        return WireJson.Read<PaymentSnapshot>(await response.Content.ReadAsStringAsync(deadline.Token));
    }
    private bool OutboxesSent() => Number("Checkout.Api", "SELECT COUNT(*) FROM outbox WHERE sent=0") == 0 &&
        Number("Orders.Service", "SELECT COUNT(*) FROM outbox WHERE sent=0") == 0 &&
        Number("WarehouseResponse.Service", "SELECT COUNT(*) FROM outbox WHERE sent=0") == 0 &&
        Number("Fulfillment.Service", "SELECT COUNT(*) FROM warehouse_file_outbox WHERE sent=0") == 0 &&
        Number("Warehouse.Simulator", "SELECT COUNT(*) FROM warehouse_shipments WHERE sent=0") == 0;
    private async Task<bool> QueuesDrained()
    {
        foreach (var (suffix, _) in Broker.Routes)
        foreach (var queue in new[] { settings.Name + "." + suffix, settings.Name + "." + suffix + ".errors" })
        {
            using var response = await management.GetAsync("/api/queues/" + settings.Name + "/" + queue, deadline.Token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            if (!json.RootElement.TryGetProperty("messages", out var messages)) return false;
            if (queue.EndsWith(".errors", StringComparison.Ordinal)) Require(messages.GetInt64() == 0, "Business error queue is not empty: " + queue);
            else
            {
                var expectedAcks = suffix == "orders.order-created.v1" ? 2 : 1;
                // Initial zero statistics cannot certify a run. Observe the real
                // deliveries acknowledged as well as a drained queue sample.
                if (messages.GetInt64() != 0 || !json.RootElement.TryGetProperty("messages_unacknowledged", out var unacked) || unacked.GetInt64() != 0 ||
                    !json.RootElement.TryGetProperty("message_stats", out var stats) || !stats.TryGetProperty("ack", out var ack) || ack.GetInt64() < expectedAcks) return false;
            }
        }
        return true;
    }
    private string[] Archived(string kind)
    {
        var path = Path.Combine(settings.Warehouse, "processed", kind);
        return Directory.Exists(path) ? Directory.GetFiles(path, "*.json") : [];
    }
    private long Count(string service, string table, Guid? id = null) => Number(service,
        "SELECT COUNT(*) FROM " + table + (id.HasValue ? " WHERE order_id=$order" : ""), id.HasValue ? [("$order", id.Value.ToString())] : []);
    private long Number(string service, string sql, params (string, object)[] parameters) => Convert.ToInt64(Query(service, sql, parameters));
    private object? Query(string service, string sql, params (string, object)[] parameters)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = settings.Database(service), Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command.ExecuteScalar();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private void SetPhase(string value) { phase = value; Console.WriteLine("E2E: " + value); }
    public ValueTask DisposeAsync() { http.Dispose(); management.Dispose(); deadline.Dispose(); return ValueTask.CompletedTask; }
}

// Read-only manual inspection, using the same SQLite runtime as the verifier.
// This creates no orders and does not stop/reset a laboratory.
static class InventoryInspection
{
    public static async Task PrintAsync(IConfiguration configuration, Guid orderId)
    {
        if (orderId == Guid.Empty || configuration["Lab:Root"] is not { } root)
            throw new ArgumentException("Supply a nonempty E2E:InventoryOrderId and Lab:Root.");
        var database = Path.Combine(Path.GetFullPath(root), "data", "Inventory.Service", "state.db");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            await using var db = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5
            }.ToString());
            await db.OpenAsync(timeout.Token);
            await using var completed = db.CreateCommand();
            completed.CommandText = "SELECT completed FROM inventory_work WHERE order_id=$order";
            completed.Parameters.AddWithValue("$order", orderId.ToString());
            if (Convert.ToInt64(await completed.ExecuteScalarAsync(timeout.Token)) == 1)
            {
                await using var rows = db.CreateCommand();
                rows.CommandText = "SELECT sku,quantity FROM inventory_reservations WHERE order_id=$order ORDER BY sku";
                rows.Parameters.AddWithValue("$order", orderId.ToString());
                await using var reader = await rows.ExecuteReaderAsync(timeout.Token);
                var reservations = new List<object>();
                while (await reader.ReadAsync(timeout.Token))
                    reservations.Add(new { sku = reader.GetString(0), quantity = reader.GetInt64(1) });
                Console.WriteLine(JsonSerializer.Serialize(new { orderId, completed = true, reservations }));
                return;
            }
            await Task.Delay(200, timeout.Token);
        }
    }
}
