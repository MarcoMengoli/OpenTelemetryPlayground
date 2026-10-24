using Microsoft.Data.Sqlite;
using Playground.Contracts;
using Playground.Infrastructure;

namespace Orders.Service;

public static class OrdersApplication
{
    public static WebApplication Build(string[] args)
    {
        var builder = FoundationHost.Create(args, "Orders.Service");
        var settings = LabSettings.Read(builder.Configuration);
        builder.Services.AddSingleton<OrdersStore>();
        builder.Services.AddSingleton(provider => new DurableOutbox(provider.GetRequiredService<OrdersStore>().Database));
        builder.Services.AddHttpClient<PaymentAuthorization>(client =>
        {
            client.BaseAddress = new Uri(builder.Configuration["Services:PaymentsBaseUrl"] ?? $"http://127.0.0.1:{settings.RabbitPort + 12}");
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        builder.Services.AddSingleton<OrderIntake>();
        builder.Services.AddHostedService<OrderCreatedConsumer>();
        builder.Services.AddHostedService<OutboxPump>();
        var app = builder.Build();
        app.Services.GetRequiredService<OrdersStore>().Initialize();
        app.MapFoundation("Orders.Service");
        app.MapOrders();
        return app;
    }

    public static void MapOrders(this WebApplication app)
    {
        app.MapGet("/internal/v1/orders/{orderId}", (string orderId, OrdersStore store, ILogger<OrdersStore> logger) =>
        {
            if (!Guid.TryParse(orderId, out var id) || id == Guid.Empty) return Error(400, "invalid_request", "A nonempty order UUID is required.");
            try
            {
                var snapshot = store.Get(id);
                return snapshot is null ? Error(404, "not_found", "Unknown order.") : Results.Json(snapshot, WireJson.Options);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Order lookup failed for {OrderId}", id);
                return exception is SqliteException { SqliteErrorCode: 5 or 6 } ? Error(503, "temporarily_unavailable", "Orders storage is temporarily busy.")
                    : Error(500, "internal_error", "Order lookup failed.");
            }
        });
    }

    private static IResult Error(int status, string code, string message) => Results.Json(new ApiError(code, message), WireJson.Options, statusCode: status);
}
