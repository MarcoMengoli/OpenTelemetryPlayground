using Microsoft.Data.Sqlite;
using Playground.Contracts;
using Playground.Infrastructure;
using Response = Playground.Contracts.WarehouseResponse;

namespace Playground.WarehouseResponses;

public sealed class ResponseConflictException() : Exception("WarehouseReference reused with incompatible shipment data.");
public sealed record AcquiredResponse(Response Response, string State, int Attempts, string? LastError, DateTimeOffset RetryAfter);

public sealed class ResponseStore(LabSettings settings)
{
    public string Database { get; } = settings.Database("WarehouseResponse.Service");
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false, DefaultTimeout = 5 }.ToString());
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
        Directory.CreateDirectory(Path.GetDirectoryName(Database)!);
        using var db = Open();
        using var command = Command(db, null, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS responses (
                reference TEXT PRIMARY KEY, response_json TEXT NOT NULL,
                state TEXT NOT NULL DEFAULT 'PendingLookup', order_id TEXT UNIQUE,
                attempts INTEGER NOT NULL DEFAULT 0, last_error TEXT, retry_after TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS file_failures (
                name TEXT PRIMARY KEY, attempts INTEGER NOT NULL, retry_after TEXT NOT NULL);
            """);
        command.ExecuteNonQuery();
        DurableOutbox.Initialize(db);
    }
    public AcquiredResponse Accept(Response response)
    {
        response.Validate();
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using (var read = Command(db, tx, "SELECT response_json FROM responses WHERE reference=$ref", ("$ref", response.WarehouseReference)))
            if (read.ExecuteScalar() is string original && WireJson.Read<Response>(original) != response) throw new ResponseConflictException();
        using (var insert = Command(db, tx, """
            INSERT INTO responses(reference,response_json,retry_after) VALUES($ref,$json,$after)
            ON CONFLICT(reference) DO NOTHING
            """, ("$ref", response.WarehouseReference), ("$json", WireJson.Write(response)), ("$after", DateTimeOffset.MinValue.ToString("O"))))
            insert.ExecuteNonQuery();
        tx.Commit();
        return Get(response.WarehouseReference)!;
    }
    public AcquiredResponse? Get(string reference)
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT response_json,state,attempts,last_error,retry_after FROM responses WHERE reference=$ref", ("$ref", reference));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }
    private static AcquiredResponse Read(SqliteDataReader reader) => new(WireJson.Read<Response>(reader.GetString(0)),
        reader.GetString(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4)));
    public IReadOnlyList<AcquiredResponse> Pending()
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT response_json,state,attempts,last_error,retry_after FROM responses WHERE state='PendingLookup' ORDER BY rowid");
        using var reader = command.ExecuteReader();
        var result = new List<AcquiredResponse>();
        while (reader.Read()) result.Add(Read(reader));
        return result;
    }
    public int StartAttempt(string reference, DateTimeOffset now)
    {
        using var db = Open();
        using var command = Command(db, null, """
            UPDATE responses SET attempts=attempts+1, last_error='lookup_interrupted',
                retry_after=$after WHERE reference=$ref AND state='PendingLookup' AND attempts<3
            RETURNING attempts
            """, ("$ref", reference), ("$after", now.AddSeconds(4).ToString("O")));
        return Convert.ToInt32(command.ExecuteScalar());
    }
    public void FailLookup(string reference, int attempt, string error, bool permanent, DateTimeOffset now)
    {
        using var db = Open();
        using var command = Command(db, null, """
            UPDATE responses SET state=$state,last_error=$error,retry_after=$after
            WHERE reference=$ref AND state='PendingLookup'
            """, ("$ref", reference), ("$state", permanent || attempt >= 3 ? "Rejected" : "PendingLookup"),
            ("$error", error), ("$after", now.AddSeconds(1 << (attempt - 1)).ToString("O")));
        command.ExecuteNonQuery();
    }
    public void Complete(Response response, WarehouseMappingResponse mapping)
    {
        mapping.Validate();
        if (response.WarehouseReference != mapping.WarehouseReference) throw new ArgumentException("Lookup reference mismatch.");
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using (var read = Command(db, tx, "SELECT state FROM responses WHERE reference=$ref", ("$ref", response.WarehouseReference)))
            if (read.ExecuteScalar() as string != "PendingLookup") return;
        using (var collision = Command(db, tx, "SELECT COUNT(*) FROM responses WHERE order_id=$order AND reference<>$ref",
            ("$order", mapping.OrderId.ToString()), ("$ref", response.WarehouseReference)))
            if (Convert.ToInt64(collision.ExecuteScalar()) != 0) throw new ResponseConflictException();
        var message = new ShipmentConfirmation(1, Guid.NewGuid(), DateTimeOffset.UtcNow, mapping.OrderId,
            response.WarehouseReference, response.ShipmentId, response.ShippedAt);
        DurableOutbox.Enqueue(db, tx, new(message.MessageId, message.OrderId, "ShipmentConfirmation.v1", "shipment.confirmed.v1", WireJson.Write(message)));
        using (var update = Command(db, tx, "UPDATE responses SET state='Ready',order_id=$order,last_error=NULL WHERE reference=$ref",
            ("$ref", response.WarehouseReference), ("$order", mapping.OrderId.ToString()))) update.ExecuteNonQuery();
        tx.Commit();
    }
    public bool CanReadFile(string name, DateTimeOffset now)
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT attempts,retry_after FROM file_failures WHERE name=$name", ("$name", name));
        using var reader = command.ExecuteReader();
        return !reader.Read() || (reader.GetInt32(0) < 3 && DateTimeOffset.Parse(reader.GetString(1)) <= now);
    }
    public int FailFile(string name, DateTimeOffset now)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using var read = Command(db, tx, "SELECT attempts FROM file_failures WHERE name=$name", ("$name", name));
        var attempt = Math.Min(3, Convert.ToInt32(read.ExecuteScalar()) + 1);
        using var write = Command(db, tx, """
            INSERT INTO file_failures VALUES($name,$attempt,$after) ON CONFLICT(name)
            DO UPDATE SET attempts=excluded.attempts,retry_after=excluded.retry_after
            """, ("$name", name), ("$attempt", attempt), ("$after", now.AddSeconds(1 << (attempt - 1)).ToString("O")));
        write.ExecuteNonQuery();
        tx.Commit();
        return attempt;
    }
    public void ClearFileFailure(string name)
    {
        using var db = Open();
        using var command = Command(db, null, "DELETE FROM file_failures WHERE name=$name", ("$name", name));
        command.ExecuteNonQuery();
    }
}
