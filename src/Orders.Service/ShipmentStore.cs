using Microsoft.Data.Sqlite;
using Playground.Contracts;
using Playground.Infrastructure;

namespace Orders.Service;

public sealed class ShipmentRejectedException(string code) : Exception(code);
public enum CompletionOperation { Capture, Invoice }
public sealed record CompletionWork(Guid OrderId, OrderStatus Status, Guid AuthorizationId, Guid? CaptureId, Money Total);

public sealed partial class OrdersStore
{
    private static SqliteCommand ShipmentCommand(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string, object?)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static void InitializeCompletion(SqliteConnection connection)
    {
        using var command = ShipmentCommand(connection, null, """
            CREATE TABLE IF NOT EXISTS shipment_inbox (
                message_id TEXT PRIMARY KEY, order_id TEXT NOT NULL, body TEXT NOT NULL,
                bound INTEGER NOT NULL DEFAULT 0, processed INTEGER NOT NULL DEFAULT 0,
                lookup_attempts INTEGER NOT NULL DEFAULT 0, blocked INTEGER NOT NULL DEFAULT 0, last_error TEXT);
            CREATE TABLE IF NOT EXISTS shipment_work (
                order_id TEXT PRIMARY KEY, warehouse_reference TEXT NOT NULL UNIQUE, shipment_id TEXT NOT NULL UNIQUE,
                shipped_at TEXT NOT NULL, capture_id TEXT UNIQUE, invoice_id TEXT UNIQUE,
                capture_attempts INTEGER NOT NULL DEFAULT 0, invoice_attempts INTEGER NOT NULL DEFAULT 0,
                capture_blocked INTEGER NOT NULL DEFAULT 0, invoice_blocked INTEGER NOT NULL DEFAULT 0);
            """);
        command.ExecuteNonQuery();
    }

    public bool AcceptShipment(ShipmentConfirmation message)
    {
        message.Validate();
        var body = WireJson.Write(message);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var order = ShipmentCommand(connection, transaction, "SELECT status FROM orders WHERE order_id=$order", ("$order", message.OrderId.ToString()));
        var status = order.ExecuteScalar() as string ?? throw new ShipmentRejectedException("shipment_unknown_order");
        if (status is not ("FulfillmentPending" or "Shipped" or "Captured" or "Completed")) throw new ShipmentRejectedException("shipment_invalid_state");
        using var receipt = ShipmentCommand(connection, transaction, "SELECT body FROM shipment_inbox WHERE message_id=$message", ("$message", message.MessageId.ToString()));
        if (receipt.ExecuteScalar() is string previous && previous != body) throw new ShipmentRejectedException("shipment_message_conflict");
        var bound = CheckShipment(connection, transaction, message);
        if (!bound && status != "FulfillmentPending") throw new ShipmentRejectedException("shipment_invalid_state");
        using var insert = ShipmentCommand(connection, transaction, """
            INSERT INTO shipment_inbox(message_id,order_id,body,bound,processed) VALUES($message,$order,$body,$bound,$done)
            ON CONFLICT(message_id) DO UPDATE SET bound=MAX(bound,excluded.bound), processed=MAX(processed,excluded.processed)
            """, ("$message", message.MessageId.ToString()), ("$order", message.OrderId.ToString()), ("$body", body), ("$bound", bound ? 1 : 0), ("$done", status == "Completed" ? 1 : 0));
        insert.ExecuteNonQuery();
        transaction.Commit();
        return status != "Completed";
    }

    private static bool CheckShipment(SqliteConnection connection, SqliteTransaction transaction, ShipmentConfirmation message)
    {
        using var command = ShipmentCommand(connection, transaction, "SELECT warehouse_reference,shipment_id,shipped_at FROM shipment_work WHERE order_id=$order", ("$order", message.OrderId.ToString()));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return false;
        if (reader.GetString(0) != message.WarehouseReference || Guid.Parse(reader.GetString(1)) != message.ShipmentId || DateTimeOffset.Parse(reader.GetString(2)) != message.ShippedAt)
            throw new ShipmentRejectedException("shipment_business_conflict");
        return true;
    }

    public bool ShipmentBound(Guid messageId)
    {
        using var connection = Open();
        using var command = ShipmentCommand(connection, null, "SELECT bound FROM shipment_inbox WHERE message_id=$message", ("$message", messageId.ToString()));
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public int StartMappingAttempt(Guid messageId)
    {
        using var connection = Open();
        using var command = ShipmentCommand(connection, null, """
            UPDATE shipment_inbox SET lookup_attempts=lookup_attempts+1
            WHERE message_id=$message AND bound=0 AND blocked=0 AND lookup_attempts<3 RETURNING lookup_attempts
            """, ("$message", messageId.ToString()));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void MappingFailed(ShipmentConfirmation message, string error, bool blocked)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var receipt = ShipmentCommand(connection, transaction, "UPDATE shipment_inbox SET last_error=$error,blocked=$blocked WHERE message_id=$message AND bound=0",
            ("$error", error), ("$blocked", blocked ? 1 : 0), ("$message", message.MessageId.ToString()));
        receipt.ExecuteNonQuery();
        using var order = ShipmentCommand(connection, transaction, """
            UPDATE orders SET last_error=$error WHERE order_id=$order AND status='FulfillmentPending'
            AND NOT EXISTS(SELECT 1 FROM shipment_work WHERE order_id=$order)
            """, ("$error", error), ("$order", message.OrderId.ToString()));
        order.ExecuteNonQuery();
        transaction.Commit();
    }

    public void BindShipment(ShipmentConfirmation message)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!CheckShipment(connection, transaction, message))
        {
            using var collision = ShipmentCommand(connection, transaction, "SELECT COUNT(*) FROM shipment_work WHERE warehouse_reference=$reference OR shipment_id=$shipment",
                ("$reference", message.WarehouseReference), ("$shipment", message.ShipmentId.ToString()));
            if (Convert.ToInt64(collision.ExecuteScalar()) != 0) throw new ShipmentRejectedException("shipment_business_conflict");
            using var insert = ShipmentCommand(connection, transaction, "INSERT INTO shipment_work(order_id,warehouse_reference,shipment_id,shipped_at) VALUES($order,$reference,$shipment,$at)",
                ("$order", message.OrderId.ToString()), ("$reference", message.WarehouseReference), ("$shipment", message.ShipmentId.ToString()), ("$at", message.ShippedAt.ToString("O")));
            insert.ExecuteNonQuery();
        }
        using var order = ShipmentCommand(connection, transaction, "UPDATE orders SET status='Shipped',last_error=NULL WHERE order_id=$order AND status='FulfillmentPending'", ("$order", message.OrderId.ToString()));
        order.ExecuteNonQuery();
        using var receipt = ShipmentCommand(connection, transaction, "UPDATE shipment_inbox SET bound=1,last_error=NULL WHERE message_id=$message", ("$message", message.MessageId.ToString()));
        receipt.ExecuteNonQuery();
        transaction.Commit();
    }

    public CompletionWork Completion(Guid orderId)
    {
        using var connection = Open();
        using var command = ShipmentCommand(connection, null, """
            SELECT o.status,o.authorization_id,s.capture_id,o.request_json FROM orders o
            JOIN shipment_work s ON s.order_id=o.order_id WHERE o.order_id=$order
            """, ("$order", orderId.ToString()));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("A validated shipment is required.");
        return new(orderId, Enum.Parse<OrderStatus>(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            OrderContent.Total(WireJson.Read<CheckoutRequest>(reader.GetString(3)).Items));
    }

    public int StartCompletionAttempt(Guid orderId, CompletionOperation operation)
    {
        var (column, status) = Step(operation);
        using var connection = Open();
        using var command = ShipmentCommand(connection, null, $"""
            UPDATE shipment_work SET {column}_attempts={column}_attempts+1
            WHERE order_id=$order AND {column}_blocked=0 AND {column}_attempts<3
            AND EXISTS(SELECT 1 FROM orders WHERE order_id=$order AND status=$status) RETURNING {column}_attempts
            """, ("$order", orderId.ToString()), ("$status", status));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void CompletionFailed(Guid orderId, CompletionOperation operation, string error, bool blocked)
    {
        var (column, status) = Step(operation);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var work = ShipmentCommand(connection, transaction, $"UPDATE shipment_work SET {column}_blocked=$blocked WHERE order_id=$order",
            ("$order", orderId.ToString()), ("$blocked", blocked ? 1 : 0));
        work.ExecuteNonQuery();
        using var order = ShipmentCommand(connection, transaction, "UPDATE orders SET last_error=$error WHERE order_id=$order AND status=$status",
            ("$order", orderId.ToString()), ("$error", error), ("$status", status));
        order.ExecuteNonQuery();
        transaction.Commit();
    }

    private static (string Column, string Status) Step(CompletionOperation operation) => operation switch
    {
        CompletionOperation.Capture => ("capture", "Shipped"), CompletionOperation.Invoice => ("invoice", "Captured"),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    public void RecordCapture(CapturePaymentResponse response)
    {
        response.Validate();
        var work = Completion(response.OrderId);
        if (response.AuthorizationId != work.AuthorizationId || response.Total != work.Total) throw new PaymentCallException("payments_invalid_response", false);
        RecordCompletion(response.OrderId, CompletionOperation.Capture, response.CaptureId);
    }

    public void RecordInvoice(InvoiceResponse response)
    {
        response.Validate();
        var work = Completion(response.OrderId);
        if (response.CaptureId != work.CaptureId || response.Total != work.Total) throw new PaymentCallException("payments_invalid_response", false);
        RecordCompletion(response.OrderId, CompletionOperation.Invoice, response.InvoiceId);
    }

    private void RecordCompletion(Guid orderId, CompletionOperation operation, Guid resultId)
    {
        var (column, status) = Step(operation);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var read = ShipmentCommand(connection, transaction, "SELECT status FROM orders WHERE order_id=$order", ("$order", orderId.ToString()));
        if (read.ExecuteScalar() as string != status) { transaction.Commit(); return; }
        using var work = ShipmentCommand(connection, transaction, $"UPDATE shipment_work SET {column}_id=$id WHERE order_id=$order", ("$id", resultId.ToString()), ("$order", orderId.ToString()));
        work.ExecuteNonQuery();
        using var order = ShipmentCommand(connection, transaction, "UPDATE orders SET status=$status,last_error=NULL WHERE order_id=$order",
            ("$order", orderId.ToString()), ("$status", operation == CompletionOperation.Capture ? "Captured" : "Completed"));
        order.ExecuteNonQuery();
        if (operation == CompletionOperation.Invoice)
        {
            using var receipt = ShipmentCommand(connection, transaction, "UPDATE shipment_inbox SET processed=1 WHERE order_id=$order AND bound=1", ("$order", orderId.ToString()));
            receipt.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<ShipmentConfirmation> PendingShipments()
    {
        using var connection = Open();
        using (var command = ShipmentCommand(connection, null, """
            UPDATE shipment_inbox SET last_error='shipment_mapping_retry_exhausted' WHERE bound=0 AND blocked=0 AND lookup_attempts>=3;
            UPDATE orders SET last_error='shipment_mapping_retry_exhausted' WHERE status='FulfillmentPending'
                AND NOT EXISTS(SELECT 1 FROM shipment_work s WHERE s.order_id=orders.order_id)
                AND EXISTS(SELECT 1 FROM shipment_inbox i WHERE i.order_id=orders.order_id AND i.bound=0 AND i.blocked=0 AND i.lookup_attempts>=3);
            UPDATE orders SET last_error='capture_retry_exhausted' WHERE status='Shipped' AND order_id IN
                (SELECT order_id FROM shipment_work WHERE capture_attempts>=3 AND capture_blocked=0);
            UPDATE orders SET last_error='invoice_retry_exhausted' WHERE status='Captured' AND order_id IN
                (SELECT order_id FROM shipment_work WHERE invoice_attempts>=3 AND invoice_blocked=0);
            """)) command.ExecuteNonQuery();
        using var read = ShipmentCommand(connection, null, """
            SELECT i.body FROM shipment_inbox i JOIN orders o ON o.order_id=i.order_id
            LEFT JOIN shipment_work s ON s.order_id=i.order_id WHERE i.processed=0 AND
            ((i.bound=0 AND i.blocked=0 AND i.lookup_attempts<3 AND o.status='FulfillmentPending') OR
             (i.bound=1 AND ((o.status='Shipped' AND s.capture_blocked=0 AND s.capture_attempts<3) OR
                             (o.status='Captured' AND s.invoice_blocked=0 AND s.invoice_attempts<3)))) ORDER BY i.rowid
            """);
        using var reader = read.ExecuteReader();
        var pending = new List<ShipmentConfirmation>();
        while (reader.Read()) pending.Add(WireJson.Read<ShipmentConfirmation>(reader.GetString(0)));
        return pending;
    }
}
