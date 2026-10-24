using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Playground.Contracts;
using Playground.Infrastructure;

namespace Inventory.Service;

public sealed class ReservationConflictException : Exception;

public sealed class InventoryStore(LabSettings settings)
{
    public string Database => settings.Database("Inventory.Service");

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Database)!);
        using var connection = LocalStore.Open(Database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS inventory_work (
                order_id TEXT PRIMARY KEY, items_json TEXT NOT NULL, source_json TEXT NOT NULL,
                completed INTEGER NOT NULL DEFAULT 0 CHECK(completed IN (0,1)),
                attempts INTEGER NOT NULL DEFAULT 0 CHECK(attempts BETWEEN 0 AND 3), last_error TEXT);
            CREATE TABLE IF NOT EXISTS inventory_inbox (
                message_id TEXT PRIMARY KEY, order_id TEXT NOT NULL, body TEXT NOT NULL,
                processed INTEGER NOT NULL CHECK(processed IN (0,1)));
            CREATE TABLE IF NOT EXISTS inventory_reservations (
                order_id TEXT NOT NULL, sku TEXT NOT NULL, quantity INTEGER NOT NULL CHECK(quantity > 0),
                unit_price TEXT NOT NULL, PRIMARY KEY(order_id,sku));
            CREATE TABLE IF NOT EXISTS inventory_stock (
                sku TEXT PRIMARY KEY, reserved INTEGER NOT NULL
                CHECK(typeof(reserved) = 'integer' AND reserved >= 0));
            """;
        command.ExecuteNonQuery();
    }

    // Persist pending work before spending a retry. Message and business identities
    // are both checked, including duplicates that arrive with a new MessageId.
    public bool Acquire(ReserveInventory message)
    {
        message.Validate();
        var items = message.Items.OrderBy(item => item.Sku, StringComparer.Ordinal)
            .Select(item => item with { UnitPrice = decimal.Parse(item.UnitPrice.ToString("G29", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) }).ToArray();
        var body = WireJson.Write(message with { Items = items });
        var payload = JsonSerializer.Serialize(items, WireJson.Options);
        using var connection = LocalStore.Open(Database);
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT body FROM inventory_inbox WHERE message_id = $message";
        command.Parameters.AddWithValue("$message", message.MessageId.ToString());
        if (command.ExecuteScalar() is string previous && previous != body) throw new ReservationConflictException();
        command.CommandText = "SELECT items_json FROM inventory_work WHERE order_id = $order";
        command.Parameters.AddWithValue("$order", message.OrderId.ToString());
        if (command.ExecuteScalar() is string original && original != payload) throw new ReservationConflictException();
        command.CommandText = "INSERT INTO inventory_work(order_id,items_json,source_json) VALUES($order,$items,$body) ON CONFLICT(order_id) DO NOTHING";
        command.Parameters.AddWithValue("$items", payload);
        command.Parameters.AddWithValue("$body", body);
        command.ExecuteNonQuery();
        command.CommandText = "SELECT completed FROM inventory_work WHERE order_id = $order";
        var completed = Convert.ToInt64(command.ExecuteScalar()) == 1;
        command.CommandText = "INSERT INTO inventory_inbox(message_id,order_id,body,processed) VALUES($message,$order,$body,$done) ON CONFLICT(message_id) DO NOTHING";
        command.Parameters.AddWithValue("$done", completed ? 1 : 0);
        command.ExecuteNonQuery();
        transaction.Commit();
        return completed;
    }

    public int StartAttempt(Guid orderId)
    {
        using var connection = LocalStore.Open(Database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE inventory_work SET attempts = attempts + 1
            WHERE order_id = $order AND completed = 0 AND attempts < 3 RETURNING attempts
            """;
        command.Parameters.AddWithValue("$order", orderId.ToString());
        return Convert.ToInt32(command.ExecuteScalar());
    }

    // All lines, aggregate quantities and inbox receipts commit together. A crash
    // before ack can only cause a duplicate delivery, never a second reservation.
    public void Complete(Guid orderId)
    {
        using var connection = LocalStore.Open(Database);
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT items_json FROM inventory_work WHERE order_id = $order AND completed = 0";
        command.Parameters.AddWithValue("$order", orderId.ToString());
        if (command.ExecuteScalar() is not string json) { transaction.Commit(); return; }
        var items = JsonSerializer.Deserialize<OrderLine[]>(json, WireJson.Options)!;
        foreach (var item in items)
        {
            command.CommandText = "INSERT INTO inventory_reservations(order_id,sku,quantity,unit_price) VALUES($order,$sku,$quantity,$price)";
            command.Parameters.AddWithValue("$sku", item.Sku);
            command.Parameters.AddWithValue("$quantity", item.Quantity);
            command.Parameters.AddWithValue("$price", item.UnitPrice.ToString(CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            command.CommandText = """
                INSERT INTO inventory_stock(sku,reserved) VALUES($sku,$quantity)
                ON CONFLICT(sku) DO UPDATE SET reserved = reserved + excluded.reserved
                """;
            command.ExecuteNonQuery();
            command.Parameters.RemoveAt("$sku");
            command.Parameters.RemoveAt("$quantity");
            command.Parameters.RemoveAt("$price");
        }
        command.CommandText = "UPDATE inventory_work SET completed = 1, last_error = NULL WHERE order_id = $order";
        command.ExecuteNonQuery();
        command.CommandText = "UPDATE inventory_inbox SET processed = 1 WHERE order_id = $order";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Failed(Guid orderId, int attempt)
    {
        using var connection = LocalStore.Open(Database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE inventory_work SET last_error = $error WHERE order_id = $order AND completed = 0";
        command.Parameters.AddWithValue("$order", orderId.ToString());
        command.Parameters.AddWithValue("$error", attempt >= 3 ? "reservation_retry_exhausted" : "reservation_temporarily_unavailable");
        command.ExecuteNonQuery();
    }

    public ReserveInventory[] Pending()
    {
        using var connection = LocalStore.Open(Database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE inventory_work SET last_error = 'reservation_retry_exhausted' WHERE completed = 0 AND attempts >= 3";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT source_json FROM inventory_work WHERE completed = 0 AND attempts < 3 ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var messages = new List<ReserveInventory>();
        while (reader.Read()) messages.Add(WireJson.Read<ReserveInventory>(reader.GetString(0)));
        return messages.ToArray();
    }
}
