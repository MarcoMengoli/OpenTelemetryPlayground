using System.Net.Http.Json;
using System.Text.Json;
using Playground.Contracts;

namespace Orders.Service;

public sealed class ShipmentMapping(HttpClient client)
{
    public async Task VerifyAsync(ShipmentConfirmation message, CancellationToken token)
    {
        using var response = await client.GetAsync("/internal/v1/warehouse-requests/" + Uri.EscapeDataString(message.WarehouseReference), token);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var retryable = status >= 500 || status is 404 or 408 or 429;
            throw new PaymentCallException(retryable ? "shipment_mapping_unavailable" : "shipment_mapping_rejected", retryable);
        }
        try
        {
            var mapping = WireJson.Read<WarehouseMappingResponse>(await response.Content.ReadAsStringAsync(token));
            if (mapping.OrderId != message.OrderId || mapping.WarehouseReference != message.WarehouseReference)
                throw new PaymentCallException("shipment_mapping_conflict", false);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        { throw new PaymentCallException("shipment_mapping_invalid_response", false); }
    }
}

public sealed class PaymentCompletion(HttpClient client)
{
    public async Task<CapturePaymentResponse> CaptureAsync(CompletionWork work, CancellationToken token)
    {
        var response = await Post<CapturePaymentRequest, CapturePaymentResponse>("captures", new(work.OrderId, work.AuthorizationId), token);
        if (response.OrderId != work.OrderId || response.AuthorizationId != work.AuthorizationId || response.Total != work.Total)
            throw new PaymentCallException("payments_invalid_response", false);
        return response;
    }

    public async Task<InvoiceResponse> InvoiceAsync(CompletionWork work, CancellationToken token)
    {
        var response = await Post<IssueInvoiceRequest, InvoiceResponse>("invoices", new(work.OrderId, work.CaptureId!.Value), token);
        if (response.OrderId != work.OrderId || response.CaptureId != work.CaptureId || response.Total != work.Total)
            throw new PaymentCallException("payments_invalid_response", false);
        return response;
    }

    private async Task<TResponse> Post<TRequest, TResponse>(string route, TRequest request, CancellationToken token)
        where TRequest : IContract where TResponse : IContract
    {
        using var response = await client.PostAsJsonAsync("/api/v1/" + route, request, WireJson.Options, token);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var retryable = status >= 500 || status is 408 or 429;
            throw new PaymentCallException(retryable ? "payments_temporarily_unavailable" : "payments_request_rejected", retryable);
        }
        try { return WireJson.Read<TResponse>(await response.Content.ReadAsStringAsync(token)); }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        { throw new PaymentCallException("payments_invalid_response", false); }
    }
}
