using Microsoft.Data.Sqlite;

namespace Playground.Infrastructure;

// Local storage record, not a business wire contract. The body is a validated v1 DTO.
public sealed record OutboxMessage(Guid MessageId, Guid OrderId, string Type, string RoutingKey, string Body);
public sealed record OutboxStatus(bool NeverAttempted, string? LastError);

public sealed class DurableOutbox(string database)
{
    public static void Initialize(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS outbox (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id TEXT NOT NULL UNIQUE,
                order_id TEXT NOT NULL,
                type TEXT NOT NULL,
                routing_key TEXT NOT NULL,
                body TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                publish_started INTEGER NOT NULL DEFAULT 0,
                sent INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                UNIQUE(order_id, type)
            );
            """;
        command.ExecuteNonQuery();
    }

    public static void Enqueue(SqliteConnection connection, SqliteTransaction transaction, OutboxMessage message)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO outbox(message_id, order_id, type, routing_key, body) VALUES($id,$order,$type,$route,$body)";
        command.Parameters.AddWithValue("$id", message.MessageId.ToString());
        command.Parameters.AddWithValue("$order", message.OrderId.ToString());
        command.Parameters.AddWithValue("$type", message.Type);
        command.Parameters.AddWithValue("$route", message.RoutingKey);
        command.Parameters.AddWithValue("$body", message.Body);
        command.ExecuteNonQuery();
    }

    // One bounded cycle per process start. Unsent identities and payloads never change.
    public void StartCycle() => Execute("UPDATE outbox SET attempts = 0 WHERE sent = 0");

    public OutboxMessage? Next()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT message_id, order_id, type, routing_key, body FROM outbox WHERE sent = 0 AND attempts < 3 ORDER BY sequence LIMIT 1";
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4)) : null;
    }

    public int StartAttempt(Guid messageId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE outbox SET attempts = attempts + 1, publish_started = 1 WHERE message_id = $id AND sent = 0 AND attempts < 3 RETURNING attempts";
        command.Parameters.AddWithValue("$id", messageId.ToString());
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void MarkSent(Guid messageId) => Execute("UPDATE outbox SET sent = 1, last_error = NULL WHERE message_id = $id", messageId);
    public void MarkFailed(Guid messageId, int attempt) => Execute(
        attempt >= 3 ? "UPDATE outbox SET last_error = 'publish_retry_exhausted' WHERE message_id = $id"
            : "UPDATE outbox SET last_error = 'publish_temporarily_unavailable' WHERE message_id = $id", messageId);

    public OutboxStatus Status(Guid orderId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(publish_started),0) FROM outbox WHERE order_id = $id";
        command.Parameters.AddWithValue("$id", orderId.ToString());
        var neverAttempted = Convert.ToInt64(command.ExecuteScalar()) == 0;
        command.CommandText = "SELECT last_error FROM outbox WHERE order_id = $id AND last_error IS NOT NULL ORDER BY sequence LIMIT 1";
        return new(neverAttempted, command.ExecuteScalar() as string);
    }

    private SqliteConnection Open() { var connection = LocalStore.Open(database); connection.Open(); return connection; }
    private void Execute(string sql, Guid? id = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id.HasValue) command.Parameters.AddWithValue("$id", id.Value.ToString());
        command.ExecuteNonQuery();
    }
}
