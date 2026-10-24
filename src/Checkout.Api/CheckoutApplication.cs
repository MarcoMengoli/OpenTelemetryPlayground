using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Playground.Contracts;
using Playground.Infrastructure;

namespace Checkout.Api;

public static class CheckoutApplication
{
    public static WebApplication Build(string[] args)
    {
        var builder = FoundationHost.Create(args, "Checkout.Api");
        var settings = LabSettings.Read(builder.Configuration);
        builder.Services.AddSingleton<CheckoutStore>();
        builder.Services.AddSingleton(provider => new DurableOutbox(provider.GetRequiredService<CheckoutStore>().Database));
        builder.Services.AddHostedService<OutboxPump>();
        builder.Services.AddHttpClient<OrdersLookup>(client =>
        {
            client.BaseAddress = new Uri(builder.Configuration["Services:OrdersBaseUrl"] ?? $"http://127.0.0.1:{settings.RabbitPort + 11}");
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        var app = builder.Build();
        app.Services.GetRequiredService<CheckoutStore>().Initialize();
        app.MapFoundation("Checkout.Api");
        app.MapCheckout();
        return app;
    }

    public static void MapCheckout(this WebApplication app)
    {
        app.MapPost("/api/v1/orders", async (HttpRequest request, CheckoutStore store, ILogger<CheckoutStore> logger) =>
        {
            CheckoutRequest input;
            try
            {
                if (!request.HasJsonContentType()) return Error(400, "invalid_request", "JSON content is required.");
                input = await JsonSerializer.DeserializeAsync<CheckoutRequest>(request.Body, WireJson.Options, request.HttpContext.RequestAborted) ?? throw new JsonException();
                input = OrderContent.Normalize(input);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or OverflowException or BadHttpRequestException)
            { return Error(400, "invalid_request", "The request does not match the checkout contract."); }
            try
            {
                store.Accept(input);
                logger.LogInformation("Checkout accepted for {OrderId}", input.OrderId);
                request.HttpContext.Response.Headers.Location = $"/api/v1/orders/{input.OrderId}";
                return Results.Json(new OrderAccepted(input.OrderId, OrderStatus.Created), WireJson.Options, statusCode: 202);
            }
            catch (CheckoutConflictException) { return Error(409, "idempotency_conflict", "OrderId belongs to different checkout data."); }
            catch (Exception exception) { return Failure(exception, logger); }
        });
        app.MapGet("/api/v1/orders/{orderId}", async (string orderId, CheckoutStore store, OrdersLookup orders, ILogger<CheckoutStore> logger, CancellationToken token) =>
        {
            if (!Guid.TryParse(orderId, out var id) || id == Guid.Empty) return Error(400, "invalid_request", "A nonempty order UUID is required.");
            try
            {
                var local = store.Get(id);
                try
                {
                    var snapshot = await orders.GetAsync(id, token);
                    if (snapshot is not null) return Results.Json(snapshot, WireJson.Options);
                    return local is not null ? Results.Json(local.Snapshot, WireJson.Options) : Error(404, "not_found", "Unknown order.");
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or ArgumentException || exception is OperationCanceledException && !token.IsCancellationRequested)
                {
                    // Once a publish was attempted its outcome can be ambiguous. Never call
                    // that order unforwarded merely because a confirm was not recorded.
                    // The dispatcher may have started publishing during the HTTP lookup.
                    local = store.Get(id);
                    return local is { NeverPublished: true } ? Results.Json(local.Snapshot, WireJson.Options)
                        : Error(503, "temporarily_unavailable", "Orders is temporarily unavailable.");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { return Failure(exception, logger); }
        });
    }

    private static IResult Failure(Exception exception, ILogger logger)
    {
        logger.LogError(exception, "Checkout operation failed");
        return exception is SqliteException { SqliteErrorCode: 5 or 6 } ? Error(503, "temporarily_unavailable", "Checkout storage is temporarily busy.")
            : Error(500, "internal_error", "Checkout operation failed.");
    }
    private static IResult Error(int status, string code, string message) => Results.Json(new ApiError(code, message), WireJson.Options, statusCode: status);
}

public sealed class OrdersLookup(HttpClient client)
{
    public async Task<OrderSnapshot?> GetAsync(Guid orderId, CancellationToken token)
    {
        using var response = await client.GetAsync($"/internal/v1/orders/{orderId}", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var snapshot = WireJson.Read<OrderSnapshot>(await response.Content.ReadAsStringAsync(token));
        if (snapshot.OrderId != orderId) throw new JsonException("Orders returned a different order.");
        return snapshot;
    }
}
