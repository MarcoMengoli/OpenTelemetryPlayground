using Microsoft.Data.Sqlite;
using Playground.Contracts;
using Playground.Infrastructure;

namespace Couriers.Api;

public sealed class CourierConflictException() : Exception("OrderId already has a different estimate request.");

public sealed class CourierStore(LabSettings settings)
{
    private readonly string path = settings.Database("Couriers.Api");
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 5 }.ToString());
        db.Open();
        return db;
    }
    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS courier_estimates (
                order_id TEXT PRIMARY KEY, request_json TEXT NOT NULL,
                response_json TEXT NOT NULL, attempts INTEGER NOT NULL);
            """;
        command.ExecuteNonQuery();
    }
    // The deterministic result and attempt count commit before any delayed/lost HTTP response.
    public (DeliveryEstimateResponse Response, int Attempt) Estimate(DeliveryEstimateRequest request)
    {
        request.Validate();
        var json = WireJson.Write(request);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using var read = db.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT request_json, response_json, attempts FROM courier_estimates WHERE order_id=$id";
        read.Parameters.AddWithValue("$id", request.OrderId.ToString());
        string? responseJson = null;
        var attempts = 0;
        using (var reader = read.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != json) throw new CourierConflictException();
                responseJson = reader.GetString(1);
                attempts = reader.GetInt32(2);
            }
        }
        var response = responseJson is null
            ? new DeliveryEstimateResponse(request.OrderId, "LOCAL-SIM", request.ShippingAddress.CountryCode == "IT" ? 2 : 5)
            : WireJson.Read<DeliveryEstimateResponse>(responseJson);
        using var write = db.CreateCommand();
        write.Transaction = tx;
        write.CommandText = """
            INSERT INTO courier_estimates(order_id,request_json,response_json,attempts) VALUES($id,$request,$response,$attempts)
            ON CONFLICT(order_id) DO UPDATE SET attempts=excluded.attempts;
            """;
        write.Parameters.AddWithValue("$id", request.OrderId.ToString());
        write.Parameters.AddWithValue("$request", json);
        write.Parameters.AddWithValue("$response", WireJson.Write(response));
        write.Parameters.AddWithValue("$attempts", attempts + 1);
        write.ExecuteNonQuery();
        tx.Commit();
        return (response, attempts + 1);
    }
}