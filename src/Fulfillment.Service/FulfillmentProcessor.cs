using System.Net;
using System.Text;
using System.Text.Json;
using Playground.Contracts;

namespace Fulfillment.Service;

// One processor per local service. Serial execution prevents two retries for the same work.
public sealed class FulfillmentProcessor(FulfillmentStore store, HttpClient courier, ILogger<FulfillmentProcessor> logger)
{
    private readonly SemaphoreSlim serial = new(1);
    public async Task<bool> ProcessAsync(Guid orderId, CancellationToken token)
    {
        await serial.WaitAsync(token);
        try
        {
            while (true)
            {
                var work = store.Get(orderId) ?? throw new InvalidOperationException("Unknown fulfillment.");
                if (work.State != "Pending") return work.State == "Ready";
                if (work.Attempts >= 3)
                {
                    store.RecordFailure(orderId, "retry_exhausted", terminal: true);
                    return false;
                }
                // 1s / 2s / 4s, including after restart. The durable budget is not reset.
                await Task.Delay(TimeSpan.FromSeconds(1 << work.Attempts), token);
                if (store.BeginAttempt(orderId) == 0) continue;
                var request = WireJson.Read<CheckoutRequest>(work.RequestJson);
                DeliveryEstimateResponse? estimate = null;
                string? error = null;
                var terminal = false;
                try
                {
                    using var content = new StringContent(WireJson.Write(new DeliveryEstimateRequest(orderId, request.ShippingAddress)), Encoding.UTF8, "application/json");
                    using var response = await courier.PostAsync("/api/v1/estimates", content, token);
                    if (!response.IsSuccessStatusCode)
                    {
                        error = "courier_http_" + (int)response.StatusCode;
                        terminal = response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests) && (int)response.StatusCode < 500;
                    }
                    else
                    {
                        estimate = WireJson.Read<DeliveryEstimateResponse>(await response.Content.ReadAsStringAsync(token));
                        if (estimate.OrderId != orderId) { error = "courier_order_mismatch"; terminal = true; }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { error = "courier_timeout"; }
                catch (HttpRequestException) { error = "courier_unavailable"; }
                catch (Exception e) when (e is JsonException or ArgumentException)
                { error = "courier_invalid_response"; terminal = true; }
                if (error is not null)
                {
                    store.RecordFailure(orderId, error, terminal);
                    logger.LogWarning("Courier attempt failed for {OrderId}: {Code}", orderId, error);
                    continue;
                }
                // A storage failure must leave the delivery unacknowledged, not become a courier failure.
                store.Complete(orderId, estimate!);
                logger.LogInformation("Fulfillment ready for {OrderId}, warehouse reference {WarehouseReference}", orderId, work.WarehouseReference);
                return true;
            }
        }
        finally { serial.Release(); }
    }
    public async Task ResumePendingAsync(CancellationToken token)
    {
        foreach (var work in store.Pending()) await ProcessAsync(work.OrderId, token);
    }
}