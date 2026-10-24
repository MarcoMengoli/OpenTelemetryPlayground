using Microsoft.Data.Sqlite;
using Playground.Contracts;
using Playground.Infrastructure;

namespace Fulfillment.Service;

public sealed class FulfillmentConflictException() : Exception("MessageId or OrderId was reused with incompatible data.");
public sealed record FulfillmentWork(Guid OrderId, string WarehouseReference, string RequestJson, string State, int Attempts, string? LastError);
public sealed record WarehouseOutboxEntry(string WarehouseReference, string PayloadJson);

public sealed class FulfillmentStore(LabSettings settings)
{
    private readonly string path = settings.Database("Fulfillment.Service");
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 5 }.ToString());
        db.Open();
        return db;
    }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = Open();
        using var command = Command(db, null, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS fulfillment_work (
                order_id TEXT PRIMARY KEY, warehouse_reference TEXT NOT NULL UNIQUE,
                request_json TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN ('Pending','Ready','Failed')),
                attempts INTEGER NOT NULL DEFAULT 0, last_error TEXT);
            CREATE TABLE IF NOT EXISTS fulfillment_inbox (
                message_id TEXT PRIMARY KEY, order_id TEXT NOT NULL, message_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS warehouse_request_file_failures (
                warehouse_reference TEXT PRIMARY KEY, attempts INTEGER NOT NULL, last_error TEXT NOT NULL, retry_after TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS warehouse_file_outbox (
                warehouse_reference TEXT PRIMARY KEY, payload_json TEXT NOT NULL, sent INTEGER NOT NULL DEFAULT 0);
            """);
        command.ExecuteNonQuery();
    }
    public void Accept(FulfillOrder message)
    {
        message.Validate();
        var messageJson = WireJson.Write(message);
        // Decimal formatting is not business identity (12.5 and 12.50 are the same price).
        var business = new CheckoutRequest(message.OrderId,
            message.Items.Select(i => i with { UnitPrice = decimal.Parse(i.UnitPrice.ToString("G29", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture) }).ToArray(),
            message.ShippingAddress);
        var requestJson = WireJson.Write(business);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using (var read = Command(db, tx, "SELECT message_json FROM fulfillment_inbox WHERE message_id=$id", ("$id", message.MessageId.ToString())))
        {
            if (read.ExecuteScalar() is string existing)
            {
                // Re-serialize normalized contract data to allow equivalent decimal representations.
                var original = WireJson.Read<FulfillOrder>(existing);
                if (original.OrderId != message.OrderId || original.OccurredAt != message.OccurredAt ||
                    !SameBusiness(original, message)) throw new FulfillmentConflictException();
                tx.Commit();
                return;
            }
        }
        using (var read = Command(db, tx, "SELECT request_json FROM fulfillment_work WHERE order_id=$id", ("$id", message.OrderId.ToString())))
        {
            if (read.ExecuteScalar() is string existing)
            {
                if (existing != requestJson) throw new FulfillmentConflictException();
            }
            else
            {
                using var insert = Command(db, tx, """
                    INSERT INTO fulfillment_work(order_id,warehouse_reference,request_json,state)
                    VALUES($id,$reference,$json,'Pending')
                    """, ("$id", message.OrderId.ToString()), ("$reference", "wh-" + Guid.NewGuid().ToString("N")), ("$json", requestJson));
                insert.ExecuteNonQuery();
            }
        }
        using var inbox = Command(db, tx, "INSERT INTO fulfillment_inbox VALUES($message,$order,$json)",
            ("$message", message.MessageId.ToString()), ("$order", message.OrderId.ToString()), ("$json", messageJson));
        inbox.ExecuteNonQuery();
        tx.Commit();
    }
    private static bool SameBusiness(FulfillOrder a, FulfillOrder b) =>
        a.ShippingAddress == b.ShippingAddress && a.Items.SequenceEqual(b.Items);

    public FulfillmentWork? Get(Guid orderId)
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT order_id,warehouse_reference,request_json,state,attempts,last_error FROM fulfillment_work WHERE order_id=$id", ("$id", orderId.ToString()));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }
    private static FulfillmentWork Read(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(0)),
        reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5));
    public IReadOnlyList<FulfillmentWork> Pending()
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT order_id,warehouse_reference,request_json,state,attempts,last_error FROM fulfillment_work WHERE state='Pending' ORDER BY rowid");
        using var reader = command.ExecuteReader();
        var results = new List<FulfillmentWork>();
        while (reader.Read()) results.Add(Read(reader));
        return results;
    }
    public int BeginAttempt(Guid orderId)
    {
        using var db = Open();
        using var command = Command(db, null, """
            UPDATE fulfillment_work SET attempts=attempts+1,last_error='attempt_in_progress'
            WHERE order_id=$id AND state='Pending' AND attempts<3 RETURNING attempts
            """, ("$id", orderId.ToString()));
        return Convert.ToInt32(command.ExecuteScalar());
    }
    public void RecordFailure(Guid orderId, string code, bool terminal)
    {
        using var db = Open();
        using var command = Command(db, null, """
            UPDATE fulfillment_work SET last_error=$error,
                state=CASE WHEN $terminal=1 OR attempts>=3 THEN 'Failed' ELSE 'Pending' END
            WHERE order_id=$id AND state='Pending'
            """, ("$id", orderId.ToString()), ("$error", code), ("$terminal", terminal ? 1 : 0));
        command.ExecuteNonQuery();
    }
    public void Complete(Guid orderId, DeliveryEstimateResponse estimate)
    {
        estimate.Validate();
        if (estimate.OrderId != orderId) throw new ArgumentException("Courier returned a different OrderId.");
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        string reference;
        CheckoutRequest request;
        using (var read = Command(db, tx, "SELECT warehouse_reference,request_json,state FROM fulfillment_work WHERE order_id=$id", ("$id", orderId.ToString())))
        using (var reader = read.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("Unknown fulfillment.");
            if (reader.GetString(2) == "Ready") { tx.Commit(); return; }
            if (reader.GetString(2) != "Pending") throw new InvalidOperationException("Fulfillment is not pending.");
            reference = reader.GetString(0);
            request = WireJson.Read<CheckoutRequest>(reader.GetString(1));
        }
        var payload = new WarehouseRequest(1, reference, request.Items, request.ShippingAddress, estimate.CourierCode, estimate.EstimatedDays);
        using (var insert = Command(db, tx, "INSERT INTO warehouse_file_outbox(warehouse_reference,payload_json) VALUES($ref,$json)",
            ("$ref", reference), ("$json", WireJson.Write(payload)))) insert.ExecuteNonQuery();
        using (var update = Command(db, tx, "UPDATE fulfillment_work SET state='Ready',last_error=NULL WHERE order_id=$id", ("$id", orderId.ToString()))) update.ExecuteNonQuery();
        tx.Commit();
    }
    // The writer marks sent only after atomic publication; pending rows survive a restart.
    public IReadOnlyList<WarehouseOutboxEntry> UnsentFiles()
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT warehouse_reference,payload_json FROM warehouse_file_outbox WHERE sent=0 ORDER BY rowid");
        using var reader = command.ExecuteReader();
        var results = new List<WarehouseOutboxEntry>();
        while (reader.Read()) results.Add(new(reader.GetString(0), reader.GetString(1)));
        return results;
    }
    public bool CanTryFile(string reference)
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT attempts,retry_after FROM warehouse_request_file_failures WHERE warehouse_reference=$ref", ("$ref", reference));
        using var reader = command.ExecuteReader();
        return !reader.Read() || (reader.GetInt32(0) < 3 && DateTimeOffset.Parse(reader.GetString(1)) <= DateTimeOffset.UtcNow);
    }
    public void MarkFileSent(string reference)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using (var command = Command(db, tx, "UPDATE warehouse_file_outbox SET sent=1 WHERE warehouse_reference=$ref", ("$ref", reference))) command.ExecuteNonQuery();
        using (var clear = Command(db, tx, "DELETE FROM warehouse_request_file_failures WHERE warehouse_reference=$ref", ("$ref", reference))) clear.ExecuteNonQuery();
        tx.Commit();
    }
    public void RecordFileFailure(string reference, string error, bool terminal)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using var read = Command(db, tx, "SELECT attempts FROM warehouse_request_file_failures WHERE warehouse_reference=$ref", ("$ref", reference));
        var attempts = terminal ? 3 : Math.Min(3, Convert.ToInt32(read.ExecuteScalar()) + 1);
        using var write = Command(db, tx, """
            INSERT INTO warehouse_request_file_failures VALUES($ref,$attempts,$error,$after)
            ON CONFLICT(warehouse_reference) DO UPDATE SET attempts=excluded.attempts,last_error=excluded.last_error,retry_after=excluded.retry_after
            """, ("$ref", reference), ("$attempts", attempts), ("$error", error),
            ("$after", DateTimeOffset.UtcNow.AddSeconds(1 << (attempts - 1)).ToString("O")));
        write.ExecuteNonQuery();
        tx.Commit();
    }    public WarehouseMappingResponse? Lookup(string reference)
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT order_id FROM fulfillment_work WHERE warehouse_reference=$reference", ("$reference", reference));
        return command.ExecuteScalar() is string id ? new(reference, Guid.Parse(id)) : null;
    }
}