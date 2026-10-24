using System.Text.Json;
using Microsoft.Data.Sqlite;
using Playground.Contracts;

namespace Payments.Api;

public static class PaymentEndpoints
{
    public static void MapPayments(this WebApplication app)
    {
        app.MapPost("/api/v1/authorizations", (HttpRequest request, PaymentStore store, PaymentSimulation simulation, ILogger<PaymentStore> logger) =>
            Post<AuthorizePaymentRequest, AuthorizationResponse>(request, store.Authorize, r => r.OrderId, simulation, logger));
        app.MapPost("/api/v1/captures", (HttpRequest request, PaymentStore store, PaymentSimulation simulation, ILogger<PaymentStore> logger) =>
            Post<CapturePaymentRequest, CapturePaymentResponse>(request, store.Capture, r => r.OrderId, simulation, logger));
        app.MapPost("/api/v1/authorization-cancellations", (HttpRequest request, PaymentStore store, PaymentSimulation simulation, ILogger<PaymentStore> logger) =>
            Post<CancelAuthorizationRequest, CancelAuthorizationResponse>(request, store.Cancel, r => r.OrderId, simulation, logger));
        app.MapPost("/api/v1/invoices", (HttpRequest request, PaymentStore store, PaymentSimulation simulation, ILogger<PaymentStore> logger) =>
            Post<IssueInvoiceRequest, InvoiceResponse>(request, store.Invoice, r => r.OrderId, simulation, logger));
        app.MapGet("/api/v1/payments/{orderId}", (string orderId, PaymentStore store, ILogger<PaymentStore> logger) =>
        {
            if (!Guid.TryParse(orderId, out var id) || id == Guid.Empty) return Error(400, "invalid_request", "A nonempty order UUID is required.");
            try
            {
                var payment = store.Get(id);
                return payment is null ? Error(404, "not_found", "Unknown payment.") : Results.Json(payment, WireJson.Options);
            }
            catch (Exception exception) { return Failure(exception, logger); }
        });
    }

    private static async Task<IResult> Post<TRequest, TResponse>(HttpRequest request, Func<TRequest, TResponse> operation,
        Func<TRequest, Guid> orderId, PaymentSimulation simulation, ILogger logger)
        where TRequest : IContract where TResponse : IContract
    {
        TRequest input;
        try
        {
            if (!request.HasJsonContentType()) return Error(400, "invalid_request", "JSON content is required.");
            input = await JsonSerializer.DeserializeAsync<TRequest>(request.Body, WireJson.Options, request.HttpContext.RequestAborted)
                ?? throw new JsonException("Null payload.");
            input.Validate();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or BadHttpRequestException)
        {
            return Error(400, "invalid_request", "The request does not match the payment contract.");
        }
        try
        {
            var response = operation(input);
            response.Validate();
            // The durable effect precedes the response. A client timeout never rolls it back.
            if (simulation.Selects(orderId(input)) && simulation.DelayMs > 0)
                await Task.Delay(simulation.DelayMs, request.HttpContext.RequestAborted);
            return Results.Json(response, WireJson.Options);
        }
        catch (OperationCanceledException) when (request.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception exception) { return Failure(exception, logger); }
    }

    private static IResult Failure(Exception exception, ILogger logger)
    {
        if (exception is PaymentException payment) return Error(payment.StatusCode, payment.Code, payment.Message);
        if (exception is SqliteException { SqliteErrorCode: 5 or 6 })
        {
            logger.LogWarning("Payments database is temporarily busy");
            return Error(503, "temporarily_unavailable", "The payment database is temporarily busy.");
        }
        logger.LogError(exception, "Payment operation failed");
        return Error(500, "internal_error", "The payment operation failed.");
    }

    private static IResult Error(int status, string code, string message) => Results.Json(new ApiError(code, message), WireJson.Options, statusCode: status);
}
