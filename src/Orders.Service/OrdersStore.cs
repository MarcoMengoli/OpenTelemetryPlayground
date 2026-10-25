using Microsoft.Data.Sqlite;
using Playground.Contracts;
using Playground.Infrastructure;

namespace Orders.Service;

public sealed class OrderMessageConflictException : Exception;

public sealed partial class OrdersStore(LabSettings settings)
{
    public string Database => settings.Database("Orders.Service");
    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Database)!);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS orders (
                order_id TEXT PRIMARY KEY,
                request_json TEXT NOT NULL,
                source_json TEXT NOT NULL,
                status TEXT NOT NULL,
                authorization_id TEXT,
                failure_code TEXT,
                last_error TEXT,
                auth_attempts INTEGER NOT NULL DEFAULT 0,
                authorization_blocked INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS order_inbox (
                message_id TEXT PRIMARY KEY,
                body TEXT NOT NULL,
                processed INTEGER NOT NULL DEFAULT 0
            );
            """;
        command.ExecuteNonQuery();
        DurableOutbox.Initialize(connection);
        InitializeCompletion(connection);
    }

    // Inbox receipt + Created work are durable before HTTP. Only processed=1 is a
    // completed inbox entry; a crash with processed=0 resumes the same operation.
    public bool Accept(OrderCreated message)
    {
        message.Validate();
        var input = OrderContent.Normalize(new(message.OrderId, message.Items, message.ShippingAddress));
        message = message with { Items = input.Items };
        var body = WireJson.Write(message);
        var request = WireJson.Write(input);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$message", message.MessageId.ToString());
        command.Parameters.AddWithValue("$order", message.OrderId.ToString());
        command.CommandText = "SELECT body FROM order_inbox WHERE message_id = $message";
        var previous = command.ExecuteScalar() as string;
        if (previous is not null && previous != body) throw new OrderMessageConflictException();
        command.CommandText = "SELECT request_json FROM orders WHERE order_id = $order";
        var existing = command.ExecuteScalar() as string;
        if (existing is not null && existing != request) throw new OrderMessageConflictException();
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$request", request);
        command.CommandText = """
            INSERT INTO order_inbox(message_id, body) VALUES($message,$body) ON CONFLICT DO NOTHING;
            INSERT INTO orders(order_id, request_json, source_json, status) VALUES($order,$request,$body,'Created') ON CONFLICT DO NOTHING;
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT status FROM orders WHERE order_id = $order";
        var completed = (string)command.ExecuteScalar()! != "Created";
        if (completed)
        {
            command.CommandText = "UPDATE order_inbox SET processed = 1 WHERE message_id = $message";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return !completed;
    }

    public int StartAuthorizationAttempt(Guid orderId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE orders SET auth_attempts = auth_attempts + 1 WHERE order_id = $order AND status = 'Created' AND authorization_blocked = 0 AND auth_attempts < 3 RETURNING auth_attempts";
        command.Parameters.AddWithValue("$order", orderId.ToString());
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void AuthorizationFailed(Guid orderId, string error, bool blocked = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE orders SET last_error = $error, authorization_blocked = $blocked WHERE order_id = $order AND status = 'Created'";
        command.Parameters.AddWithValue("$order", orderId.ToString());
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$blocked", blocked ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public void CompleteAuthorization(OrderCreated message, AuthorizationResponse response)
    {
        response.Validate();
        if (response.OrderId != message.OrderId) throw new ArgumentException("Authorization order mismatch.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$order", message.OrderId.ToString());
        command.CommandText = "SELECT status FROM orders WHERE order_id = $order";
        var status = command.ExecuteScalar() as string ?? throw new InvalidOperationException("Order must be acquired before authorization.");
        if (status == "Created")
        {
            command.Parameters.AddWithValue("$authorization", (object?)response.AuthorizationId?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$failure", (object?)response.DeclineCode ?? DBNull.Value);
            command.CommandText = response.Decision == PaymentDecision.Declined
                ? "UPDATE orders SET status = 'PaymentDeclined', failure_code = $failure, last_error = NULL WHERE order_id = $order"
                : "UPDATE orders SET status = 'PaymentAuthorized', authorization_id = $authorization, last_error = NULL WHERE order_id = $order";
            command.ExecuteNonQuery();
            if (response.Decision == PaymentDecision.Authorized)
            {
                var occurredAt = DateTimeOffset.UtcNow;
                var reserve = new ReserveInventory(1, Guid.NewGuid(), occurredAt, message.OrderId, message.Items);
                var fulfill = new FulfillOrder(1, Guid.NewGuid(), occurredAt, message.OrderId, message.Items, message.ShippingAddress);
                DurableOutbox.Enqueue(connection, transaction, new(reserve.MessageId, reserve.OrderId, "ReserveInventory.v1", "inventory.reserve.v1", WireJson.Write(reserve)));
                DurableOutbox.Enqueue(connection, transaction, new(fulfill.MessageId, fulfill.OrderId, "FulfillOrder.v1", "fulfillment.fulfill.v1", WireJson.Write(fulfill)));
                command.CommandText = "UPDATE orders SET status = 'FulfillmentPending' WHERE order_id = $order";
                command.ExecuteNonQuery();
            }
        }
        command.CommandText = "UPDATE order_inbox SET processed = 1 WHERE message_id = $message";
        command.Parameters.AddWithValue("$message", message.MessageId.ToString());
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public OrderSnapshot? Get(Guid orderId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.status, o.failure_code, o.last_error, o.authorization_id, s.shipment_id, s.capture_id, s.invoice_id
            FROM orders o LEFT JOIN shipment_work s ON s.order_id = o.order_id WHERE o.order_id = $order
            """;
        command.Parameters.AddWithValue("$order", orderId.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var status = Enum.Parse<OrderStatus>(reader.GetString(0));
        var failure = reader.IsDBNull(1) ? null : reader.GetString(1);
        var error = reader.IsDBNull(2) ? null : reader.GetString(2);
        Guid? authorization = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3));
        return new(orderId, status, failure, status == OrderStatus.Completed ? null : error ?? new DurableOutbox(Database).Status(orderId).LastError,
            authorization, Id(4), Id(5), Id(6));

        Guid? Id(int index) => reader.IsDBNull(index) ? null : Guid.Parse(reader.GetString(index));
    }

    public IReadOnlyList<OrderCreated> StartRecoveryCycle()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        // Permanent HTTP/protocol errors are not retried automatically on restart.
        command.CommandText = "UPDATE orders SET last_error = 'payment_retry_exhausted' WHERE status = 'Created' AND authorization_blocked = 0 AND auth_attempts >= 3";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT source_json FROM orders WHERE status = 'Created' AND authorization_blocked = 0 AND auth_attempts < 3 ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var pending = new List<OrderCreated>();
        while (reader.Read()) pending.Add(WireJson.Read<OrderCreated>(reader.GetString(0)));
        return pending;
    }

    private SqliteConnection Open() { var connection = LocalStore.Open(Database); connection.Open(); return connection; }
}
