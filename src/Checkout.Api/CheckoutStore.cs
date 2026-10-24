using Microsoft.Data.Sqlite;
using Playground.Contracts;
using Playground.Infrastructure;

namespace Checkout.Api;

public sealed class CheckoutConflictException : Exception;
public sealed record LocalAcceptance(OrderSnapshot Snapshot, bool NeverPublished);

public sealed class CheckoutStore(LabSettings settings)
{
    public string Database => settings.Database("Checkout.Api");
    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Database)!);
        using var connection = LocalStore.Open(Database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS checkouts (order_id TEXT PRIMARY KEY, request_json TEXT NOT NULL)";
        command.ExecuteNonQuery();
        DurableOutbox.Initialize(connection);
    }

    public void Accept(CheckoutRequest request)
    {
        request = OrderContent.Normalize(request);
        var body = WireJson.Write(request);
        using var connection = LocalStore.Open(Database);
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT request_json FROM checkouts WHERE order_id = $id";
        command.Parameters.AddWithValue("$id", request.OrderId.ToString());
        var previous = command.ExecuteScalar() as string;
        if (previous is not null)
        {
            if (previous != body) throw new CheckoutConflictException();
            transaction.Commit();
            return;
        }
        command.CommandText = "INSERT INTO checkouts(order_id, request_json) VALUES($id,$body)";
        command.Parameters.AddWithValue("$body", body);
        command.ExecuteNonQuery();
        var created = new OrderCreated(1, Guid.NewGuid(), DateTimeOffset.UtcNow, request.OrderId, request.Items, request.ShippingAddress);
        DurableOutbox.Enqueue(connection, transaction, new(created.MessageId, created.OrderId, "OrderCreated.v1", "order.created.v1", WireJson.Write(created)));
        transaction.Commit();
    }

    public LocalAcceptance? Get(Guid orderId)
    {
        using var connection = LocalStore.Open(Database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM checkouts WHERE order_id = $id";
        command.Parameters.AddWithValue("$id", orderId.ToString());
        if (command.ExecuteScalar() is null) return null;
        var status = new DurableOutbox(Database).Status(orderId);
        return new(new(orderId, OrderStatus.Created, null, status.LastError, null, null, null, null), status.NeverAttempted);
    }
}
