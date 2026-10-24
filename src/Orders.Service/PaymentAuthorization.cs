using System.Net.Http.Json;
using System.Text.Json;
using Playground.Contracts;
using Playground.Infrastructure;

namespace Orders.Service;

public sealed class PaymentCallException(string code, bool retryable) : Exception(code)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public sealed class PaymentAuthorization(HttpClient client)
{
    public async Task<AuthorizationResponse> AuthorizeAsync(OrderCreated order, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/authorizations", new AuthorizePaymentRequest(order.OrderId, OrderContent.Total(order.Items)), WireJson.Options, token);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            throw new PaymentCallException(status >= 500 || status is 408 or 429 ? "payments_temporarily_unavailable" : "payments_request_rejected", status >= 500 || status is 408 or 429);
        }
        try
        {
            var authorization = WireJson.Read<AuthorizationResponse>(await response.Content.ReadAsStringAsync(token));
            if (authorization.OrderId != order.OrderId) throw new JsonException("Authorization order mismatch.");
            return authorization;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        { throw new PaymentCallException("payments_invalid_response", retryable: false); }
    }
}

public sealed class OrderIntake(OrdersStore store, PaymentAuthorization payments, ILogger<OrderIntake> logger)
{
    public async Task<bool> ProcessAsync(OrderCreated message, CancellationToken token)
    {
        if (!store.Accept(message)) return true;
        int attempt;
        while ((attempt = store.StartAuthorizationAttempt(message.OrderId)) != 0)
        {
            AuthorizationResponse authorization;
            try { authorization = await payments.AuthorizeAsync(message, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (PaymentCallException exception) when (!exception.Retryable)
            {
                store.AuthorizationFailed(message.OrderId, exception.Code, blocked: true);
                logger.LogError("Payment authorization blocked for {OrderId}: {Error}", message.OrderId, exception.Code);
                return false;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException || exception is PaymentCallException { Retryable: true })
            {
                store.AuthorizationFailed(message.OrderId, attempt == 3 ? "payment_retry_exhausted" : "payments_temporarily_unavailable");
                logger.LogWarning("Payment authorization failed for {OrderId}, attempt {Attempt}", message.OrderId, attempt);
                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), token);
                continue;
            }
            store.CompleteAuthorization(message, authorization);
            logger.LogInformation("Order {OrderId}: payment {Decision}; downstream requests persisted when authorized", message.OrderId, authorization.Decision);
            return true;
        }
        return false;
    }
}
