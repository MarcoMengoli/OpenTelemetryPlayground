using Microsoft.Data.Sqlite;
using Playground.Contracts;
using Playground.Infrastructure;

namespace Warehouse.Simulator;

public sealed class WarehouseRequestConflictException() : Exception("WarehouseReference reused for an incompatible request.");
public sealed record WarehouseShipment(string Reference, string ResponseJson, DateTimeOffset AcceptedAt);
public sealed record WarehouseFileFailure(int Attempts, string LastError, DateTimeOffset RetryAfter);

public sealed class SimulatorStore(LabSettings settings)
{
    private readonly string path = settings.Database("Warehouse.Simulator");
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
            CREATE TABLE IF NOT EXISTS warehouse_shipments (
                reference TEXT PRIMARY KEY, request_json TEXT NOT NULL, response_json TEXT NOT NULL,
                accepted_at TEXT NOT NULL, sent INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS warehouse_file_failures (
                file_key TEXT PRIMARY KEY, attempts INTEGER NOT NULL, last_error TEXT NOT NULL, retry_after TEXT NOT NULL);
            """);
        command.ExecuteNonQuery();
    }
    public WarehouseResponse Accept(WarehouseRequest request, DateTimeOffset now)
    {
        request.Validate();
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        WarehouseResponse response;
        using (var command = Command(db, tx, "SELECT request_json,response_json FROM warehouse_shipments WHERE reference=$ref", ("$ref", request.WarehouseReference)))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                var original = WireJson.Read<WarehouseRequest>(reader.GetString(0));
                if (original.CourierCode != request.CourierCode || original.EstimatedDays != request.EstimatedDays ||
                    original.ShippingAddress != request.ShippingAddress || !original.Items.SequenceEqual(request.Items))
                    throw new WarehouseRequestConflictException();
                response = WireJson.Read<WarehouseResponse>(reader.GetString(1));
            }
            else response = new(1, request.WarehouseReference, Guid.NewGuid(), now);
        }
        using (var command = Command(db, tx, """
            INSERT INTO warehouse_shipments(reference,request_json,response_json,accepted_at) VALUES($ref,$request,$response,$at)
            ON CONFLICT(reference) DO UPDATE SET sent=0;
            """, ("$ref", request.WarehouseReference), ("$request", WireJson.Write(request)),
            ("$response", WireJson.Write(response)), ("$at", now.ToString("O")))) command.ExecuteNonQuery();
        // A repeated input requests replay, but never clears a blocked output's error budget.
        tx.Commit();
        return response;
    }
    public IReadOnlyList<WarehouseShipment> Pending()
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT reference,response_json,accepted_at FROM warehouse_shipments WHERE sent=0 ORDER BY rowid");
        using var reader = command.ExecuteReader();
        var result = new List<WarehouseShipment>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2))));
        return result;
    }
    public void MarkSent(string reference)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using (var command = Command(db, tx, "UPDATE warehouse_shipments SET sent=1 WHERE reference=$ref", ("$ref", reference))) command.ExecuteNonQuery();
        using (var clear = Command(db, tx, "DELETE FROM warehouse_file_failures WHERE file_key=$key", ("$key", "output:" + reference))) clear.ExecuteNonQuery();
        tx.Commit();
    }
    public WarehouseFileFailure? Failure(string key)
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT attempts,last_error,retry_after FROM warehouse_file_failures WHERE file_key=$key", ("$key", key));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt32(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2))) : null;
    }
    public bool CanTry(string key, DateTimeOffset now) => Failure(key) is not { } failure || (failure.Attempts < 3 && failure.RetryAfter <= now);
    public void Fail(string key, string error, bool terminal, DateTimeOffset now)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using var read = Command(db, tx, "SELECT attempts FROM warehouse_file_failures WHERE file_key=$key", ("$key", key));
        var attempts = terminal ? 3 : Math.Min(3, Convert.ToInt32(read.ExecuteScalar()) + 1);
        using var write = Command(db, tx, """
            INSERT INTO warehouse_file_failures VALUES($key,$attempts,$error,$after)
            ON CONFLICT(file_key) DO UPDATE SET attempts=excluded.attempts,last_error=excluded.last_error,retry_after=excluded.retry_after
            """, ("$key", key), ("$attempts", attempts), ("$error", error), ("$after", now.AddSeconds(1 << (attempts - 1)).ToString("O")));
        write.ExecuteNonQuery();
        tx.Commit();
    }
    public void ClearFailure(string key)
    {
        using var db = Open();
        using var command = Command(db, null, "DELETE FROM warehouse_file_failures WHERE file_key=$key", ("$key", key));
        command.ExecuteNonQuery();
    }
}