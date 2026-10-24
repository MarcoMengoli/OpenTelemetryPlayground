using System.Globalization;
using Microsoft.Data.Sqlite;
using Playground.Contracts;
using Playground.Infrastructure;

namespace Payments.Api;

public sealed class PaymentException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

// This service owns this database. A write transaction serializes competing HTTP requests.
public sealed class PaymentStore(LabSettings settings, PaymentSimulation simulation, ILogger<PaymentStore> logger)
{
    private string Database => settings.Database("Payments.Api");

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Database)!);
        using var connection = LocalStore.Open(Database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS payments (
                order_id TEXT PRIMARY KEY,
                amount TEXT NOT NULL,
                currency TEXT NOT NULL CHECK(currency = 'EUR'),
                status TEXT NOT NULL CHECK(status IN ('Authorized','Declined','Cancelled','Captured','Invoiced')),
                decline_code TEXT,
                authorization_id TEXT UNIQUE,
                capture_id TEXT UNIQUE,
                invoice_id TEXT UNIQUE
            );
            CREATE TABLE IF NOT EXISTS payment_attempts (
                operation TEXT NOT NULL,
                operation_key TEXT NOT NULL,
                request_data TEXT NOT NULL,
                attempts INTEGER NOT NULL,
                PRIMARY KEY(operation, operation_key)
            );
            """;
        command.ExecuteNonQuery();
    }

    public AuthorizationResponse Authorize(AuthorizePaymentRequest request)
    {
        request.Validate();
        return Write("authorize", request.OrderId, (connection, transaction) =>
        {
            var payment = Find(connection, transaction, "order_id", request.OrderId);
            if (payment is not null)
            {
                if (payment.Total != request.Total) throw Conflict();
                return Authorization(payment);
            }
            // Normalize decimal scale: 12.5 and 12.50 describe the same amount.
            Attempt(connection, transaction, "authorize", request.OrderId, request.OrderId,
                request.Total.Amount.ToString("G29", CultureInfo.InvariantCulture) + "|" + request.Total.Currency);
            var declined = simulation.Declines(request.OrderId);
            payment = new(request.OrderId, declined ? PaymentStatus.Declined : PaymentStatus.Authorized,
                declined ? "simulated_decline" : null, declined ? null : Guid.NewGuid(), null, null, request.Total);
            Save(connection, transaction, payment);
            return Authorization(payment);
        });
    }

    public CapturePaymentResponse Capture(CapturePaymentRequest request)
    {
        request.Validate();
        return Write<CapturePaymentResponse>("capture", request.OrderId, (connection, transaction) =>
        {
            var payment = Find(connection, transaction, "authorization_id", request.AuthorizationId) ?? throw InvalidState();
            if (payment.OrderId != request.OrderId) throw Conflict();
            if (payment.Status == PaymentStatus.Cancelled) throw InvalidState();
            if (payment.CaptureId is null)
            {
                Attempt(connection, transaction, "capture", request.AuthorizationId, request.OrderId, request.OrderId.ToString());
                payment = payment with { Status = PaymentStatus.Captured, CaptureId = Guid.NewGuid() };
                Save(connection, transaction, payment);
            }
            return new(payment.OrderId, request.AuthorizationId, payment.CaptureId.Value, payment.Total);
        });
    }

    public CancelAuthorizationResponse Cancel(CancelAuthorizationRequest request)
    {
        request.Validate();
        return Write<CancelAuthorizationResponse>("cancel", request.OrderId, (connection, transaction) =>
        {
            var payment = Find(connection, transaction, "authorization_id", request.AuthorizationId) ?? throw InvalidState();
            if (payment.OrderId != request.OrderId) throw Conflict();
            if (payment.Status is PaymentStatus.Captured or PaymentStatus.Invoiced) throw InvalidState();
            if (payment.Status != PaymentStatus.Cancelled)
            {
                Attempt(connection, transaction, "cancel", request.AuthorizationId, request.OrderId, request.OrderId.ToString());
                payment = payment with { Status = PaymentStatus.Cancelled };
                Save(connection, transaction, payment);
            }
            return new(payment.OrderId, request.AuthorizationId, PaymentStatus.Cancelled);
        });
    }

    public InvoiceResponse Invoice(IssueInvoiceRequest request)
    {
        request.Validate();
        return Write<InvoiceResponse>("invoice", request.OrderId, (connection, transaction) =>
        {
            var payment = Find(connection, transaction, "capture_id", request.CaptureId) ?? throw InvalidState();
            if (payment.OrderId != request.OrderId) throw Conflict();
            if (payment.InvoiceId is null)
            {
                Attempt(connection, transaction, "invoice", request.CaptureId, request.OrderId, request.OrderId.ToString());
                payment = payment with { Status = PaymentStatus.Invoiced, InvoiceId = Guid.NewGuid() };
                Save(connection, transaction, payment);
            }
            return new(payment.OrderId, request.CaptureId, payment.InvoiceId.Value, payment.Total);
        });
    }

    public PaymentSnapshot? Get(Guid orderId)
    {
        Check.Id(orderId);
        using var connection = LocalStore.Open(Database);
        connection.Open();
        return Find(connection, null, "order_id", orderId);
    }

    private T Write<T>(string operation, Guid orderId, Func<SqliteConnection, SqliteTransaction, T> action)
    {
        using var connection = LocalStore.Open(Database);
        connection.Open();
        // IMMEDIATE, not a deferred read followed by an unsafe lock upgrade.
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = action(connection, transaction);
        transaction.Commit();
        logger.LogInformation("Payment {Operation} resolved for {OrderId}", operation, orderId);
        return result;
    }

    private void Attempt(SqliteConnection connection, SqliteTransaction transaction, string operation, Guid key, Guid orderId, string data)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$key", key.ToString());
        command.CommandText = "SELECT request_data FROM payment_attempts WHERE operation = $operation AND operation_key = $key";
        var previous = command.ExecuteScalar() as string;
        if (previous is not null && previous != data) throw Conflict();
        command.Parameters.AddWithValue("$data", data);
        command.CommandText = """
            INSERT INTO payment_attempts(operation, operation_key, request_data, attempts)
            VALUES($operation, $key, $data, 1)
            ON CONFLICT(operation, operation_key) DO UPDATE SET attempts = attempts + 1
            RETURNING attempts
            """;
        var attempt = Convert.ToInt64(command.ExecuteScalar());
        if (!simulation.Fails(orderId, attempt)) return;
        // Persist failure budget even when the business operation has no effect yet.
        transaction.Commit();
        logger.LogWarning("Simulated temporary payment failure for {OrderId}, operation {Operation}, attempt {Attempt}", orderId, operation, attempt);
        throw new PaymentException(503, "temporarily_unavailable", "Simulated temporary payment failure.");
    }

    private static PaymentSnapshot? Find(SqliteConnection connection, SqliteTransaction? transaction, string column, Guid key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Column is a constant supplied only by this class; all input is parameterized.
        command.CommandText = $"SELECT order_id, status, decline_code, authorization_id, capture_id, invoice_id, amount, currency FROM payments WHERE {column} = $key";
        command.Parameters.AddWithValue("$key", key.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(Guid.Parse(reader.GetString(0)), Enum.Parse<PaymentStatus>(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2), Id(3), Id(4), Id(5),
            new(decimal.Parse(reader.GetString(6), CultureInfo.InvariantCulture), reader.GetString(7)));

        Guid? Id(int index) => reader.IsDBNull(index) ? null : Guid.Parse(reader.GetString(index));
    }

    private static void Save(SqliteConnection connection, SqliteTransaction transaction, PaymentSnapshot payment)
    {
        payment.Validate();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO payments(order_id, amount, currency, status, decline_code, authorization_id, capture_id, invoice_id)
            VALUES($order, $amount, $currency, $status, $decline, $authorization, $capture, $invoice)
            ON CONFLICT(order_id) DO UPDATE SET status = excluded.status,
                capture_id = excluded.capture_id, invoice_id = excluded.invoice_id
            """;
        command.Parameters.AddWithValue("$order", payment.OrderId.ToString());
        command.Parameters.AddWithValue("$amount", payment.Total.Amount.ToString("G29", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$currency", payment.Total.Currency);
        command.Parameters.AddWithValue("$status", payment.Status.ToString());
        command.Parameters.AddWithValue("$decline", (object?)payment.DeclineCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$authorization", (object?)payment.AuthorizationId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$capture", (object?)payment.CaptureId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$invoice", (object?)payment.InvoiceId?.ToString() ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static AuthorizationResponse Authorization(PaymentSnapshot payment) =>
        new(payment.OrderId, payment.AuthorizationId, payment.Status == PaymentStatus.Declined ? PaymentDecision.Declined : PaymentDecision.Authorized, payment.DeclineCode);
    private static PaymentException Conflict() => new(409, "idempotency_conflict", "The operation identity belongs to different request data.");
    private static PaymentException InvalidState() => new(409, "invalid_state", "The payment cannot perform this operation in its current state.");
}
