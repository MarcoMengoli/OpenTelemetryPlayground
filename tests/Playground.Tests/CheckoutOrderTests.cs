using System.Net;
using System.Net.Http.Json;
using System.Text;
using Checkout.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orders.Service;
using Playground.Contracts;
using Playground.Infrastructure;
using RabbitMQ.Client;
using Xunit;

namespace Playground.Tests;

public sealed class CheckoutOrderTests
{
    [Fact]
    public async Task Checkout_acceptance_and_outbox_survive_concurrent_HTTP_retries_and_restart()
    {
        using var data = new OrderTestData();
        var request = OrderTestData.Request();
        OutboxMessage original;
        await using (var host = await OrderTestHost.Checkout(data.Settings, new("http://127.0.0.1:1")))
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => host.Client.PostAsJsonAsync("/api/v1/orders", request, WireJson.Options)));
            try
            {
                foreach (var response in responses)
                {
                    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                    Assert.Equal($"/api/v1/orders/{request.OrderId}", response.Headers.Location!.ToString());
                    Assert.Equal(new(request.OrderId, OrderStatus.Created), WireJson.Read<OrderAccepted>(await response.Content.ReadAsStringAsync()));
                }
            }
            finally { foreach (var response in responses) response.Dispose(); }
            original = new DurableOutbox(data.Settings.Database("Checkout.Api")).Next()!;
            Assert.Equal(1, data.Scalar("Checkout.Api", "SELECT COUNT(*) FROM checkouts"));
            Assert.Equal(1, data.Scalar("Checkout.Api", "SELECT COUNT(*) FROM outbox"));
        }
        await using var restarted = await OrderTestHost.Checkout(data.Settings, new("http://127.0.0.1:1"));
        using var retry = await restarted.Client.PostAsJsonAsync("/api/v1/orders", request with { Items = [new("BOOK", 2, 12.5m)] }, WireJson.Options);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal(original, new DurableOutbox(data.Settings.Database("Checkout.Api")).Next());
        using var conflict = await restarted.Client.PostAsJsonAsync("/api/v1/orders", request with { Items = [new("BOOK", 3, 12.50m)] }, WireJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency_conflict", WireJson.Read<ApiError>(await conflict.Content.ReadAsStringAsync()).Code);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("{\"unexpected\":1}")]
    public async Task Invalid_checkout_JSON_is_rejected_without_acceptance(string body)
    {
        using var data = new OrderTestData();
        await using var host = await OrderTestHost.Checkout(data.Settings, new("http://127.0.0.1:1"));
        using var response = await host.Client.PostAsync("/api/v1/orders", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", WireJson.Read<ApiError>(await response.Content.ReadAsStringAsync()).Code);
        Assert.Equal(0, data.Scalar("Checkout.Api", "SELECT COUNT(*) FROM checkouts"));
        Assert.Equal(0, data.Scalar("Checkout.Api", "SELECT COUNT(*) FROM outbox"));
    }

    [Fact]
    public async Task Invalid_business_values_and_overflow_are_400_and_enqueue_nothing()
    {
        using var data = new OrderTestData();
        await using var host = await OrderTestHost.Checkout(data.Settings, new("http://127.0.0.1:1"));
        var valid = OrderTestData.Request();
        var invalids = new[]
        {
            valid with { OrderId = Guid.Empty },
            valid with { Items = [] },
            valid with { Items = [new("SKU", 0, 1)] },
            valid with { Items = [new("SKU", 1, 1.001m)] },
            valid with { Items = [new("SKU", 1, 1), new("SKU", 2, 1)] },
            valid with { Items = [new("SKU", 2, decimal.MaxValue)] },
            valid with { ShippingAddress = valid.ShippingAddress with { CountryCode = "it" } }
        };
        foreach (var input in invalids)
        {
            using var response = await host.Client.PostAsJsonAsync("/api/v1/orders", input, WireJson.Options);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_request", WireJson.Read<ApiError>(await response.Content.ReadAsStringAsync()).Code);
        }
        Assert.Equal(0, data.Scalar("Checkout.Api", "SELECT COUNT(*) FROM checkouts"));
    }

    [Fact]
    public async Task Acceptance_rolls_back_when_outbox_insert_fails()
    {
        using var data = new OrderTestData();
        await using var host = await OrderTestHost.Checkout(data.Settings, new("http://127.0.0.1:1"));
        data.Execute("Checkout.Api", "CREATE TRIGGER test_fail BEFORE INSERT ON outbox BEGIN SELECT RAISE(ABORT, 'test failure'); END");
        using var response = await host.Client.PostAsJsonAsync("/api/v1/orders", OrderTestData.Request(), WireJson.Options);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("internal_error", WireJson.Read<ApiError>(await response.Content.ReadAsStringAsync()).Code);
        Assert.Equal(0, data.Scalar("Checkout.Api", "SELECT COUNT(*) FROM checkouts"));
        Assert.Equal(0, data.Scalar("Checkout.Api", "SELECT COUNT(*) FROM outbox"));
    }

    [Fact]
    public async Task Checkout_lookup_handles_pending_unknown_remote_state_and_ambiguous_forwarding()
    {
        using var data = new OrderTestData();
        await using var orders = await OrderTestHost.Orders(data.Settings);
        await using var checkout = await OrderTestHost.Checkout(data.Settings, orders.Client.BaseAddress!);
        var input = OrderTestData.Request();
        using var accepted = await checkout.Client.PostAsJsonAsync("/api/v1/orders", input, WireJson.Options);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(OrderStatus.Created, (await checkout.Snapshot(input.OrderId)).Status);
        using var unknown = await checkout.Client.GetAsync($"/api/v1/orders/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var invalid = await orders.Client.GetAsync("/internal/v1/orders/not-a-guid");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var message = WireJson.Read<OrderCreated>(new DurableOutbox(data.Settings.Database("Checkout.Api")).Next()!.Body);
        using var paymentData = new PaymentData();
        await using var payments = await PaymentHost.Start(paymentData);
        var store = orders.App.Services.GetRequiredService<OrdersStore>();
        var intake = OrderTestData.Intake(store, payments.Client.BaseAddress!);
        Assert.True(await intake.ProcessAsync(message, CancellationToken.None));
        Assert.Equal(OrderStatus.FulfillmentPending, (await checkout.Snapshot(input.OrderId)).Status);
        // Losing Orders before any publish attempt permits the local acceptance fallback.
        await orders.App.StopAsync();
        Assert.Equal(OrderStatus.Created, (await checkout.Snapshot(input.OrderId)).Status);
        var outbox = new DurableOutbox(data.Settings.Database("Checkout.Api"));
        _ = outbox.StartAttempt(message.MessageId);
        outbox.StartCycle(); // Restart must not erase the fact that a publish was attempted.
        Assert.Equal(1, data.Scalar("Checkout.Api", "SELECT attempts FROM outbox"));
        using var unavailable = await checkout.Client.GetAsync($"/api/v1/orders/{input.OrderId}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("temporarily_unavailable", WireJson.Read<ApiError>(await unavailable.Content.ReadAsStringAsync()).Code);
    }

    [Fact]
    public async Task Checkout_lookup_rechecks_publication_after_Orders_fails_during_the_request()
    {
        using var data = new OrderTestData();
        var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var orders = await OrderTestHost.Stub(app => app.MapGet("/internal/v1/orders/{orderId}", async (CancellationToken token) =>
        {
            lookupStarted.TrySetResult();
            await failLookup.Task.WaitAsync(token);
            return Microsoft.AspNetCore.Http.Results.StatusCode(503);
        }));
        await using var checkout = await OrderTestHost.Checkout(data.Settings, orders.Client.BaseAddress!, TimeSpan.FromSeconds(5));
        var input = OrderTestData.Request();
        using var accepted = await checkout.Client.PostAsJsonAsync("/api/v1/orders", input, WireJson.Options);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var outbox = new DurableOutbox(data.Settings.Database("Checkout.Api"));
        var message = outbox.Next()!;
        Assert.True(outbox.Status(input.OrderId).NeverAttempted);

        var lookup = checkout.Client.GetAsync($"/api/v1/orders/{input.OrderId}");
        try
        {
            // Orders has received the request, so Checkout has already read the
            // initial acceptance. A concurrent dispatcher now starts publishing.
            await lookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, outbox.StartAttempt(message.MessageId));
        }
        finally { failLookup.TrySetResult(); }

        using var response = await lookup;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("temporarily_unavailable", WireJson.Read<ApiError>(await response.Content.ReadAsStringAsync()).Code);
    }

    [Fact]
    public async Task Orders_deduplicate_transport_and_logical_duplicates_and_reject_conflicts()
    {
        using var data = new OrderTestData();
        var store = data.NewOrders();
        using var paymentData = new PaymentData();
        await using var payments = await PaymentHost.Start(paymentData);
        var intake = OrderTestData.Intake(store, payments.Client.BaseAddress!);
        var message = OrderTestData.Message();
        Assert.True(await intake.ProcessAsync(message, CancellationToken.None));
        var snapshot = store.Get(message.OrderId);
        Assert.True(await intake.ProcessAsync(message, CancellationToken.None));
        Assert.True(await intake.ProcessAsync(message with { MessageId = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow }, CancellationToken.None));
        Assert.Equal(snapshot, store.Get(message.OrderId));
        Assert.Equal(2, data.Scalar("Orders.Service", "SELECT COUNT(*) FROM outbox"));
        Assert.Equal(2, data.Scalar("Orders.Service", "SELECT COUNT(*) FROM order_inbox WHERE processed = 1"));
        Assert.Equal(1, paymentData.Count("SELECT COUNT(*) FROM payments"));
        Assert.Throws<OrderMessageConflictException>(() => store.Accept(message with { OrderId = Guid.NewGuid() }));
        Assert.Throws<OrderMessageConflictException>(() => store.Accept(message with { MessageId = Guid.NewGuid(), Items = [new("OTHER", 1, 1)] }));
        Assert.Equal(2, data.Scalar("Orders.Service", "SELECT COUNT(*) FROM order_inbox"));
    }

    [Fact]
    public async Task Both_outbox_records_and_authorization_state_roll_back_together_then_recover()
    {
        using var data = new OrderTestData();
        var store = data.NewOrders();
        using var paymentData = new PaymentData();
        await using var payments = await PaymentHost.Start(paymentData);
        var message = OrderTestData.Message();
        data.Execute("Orders.Service", "CREATE TRIGGER test_fail BEFORE INSERT ON outbox WHEN NEW.type = 'FulfillOrder.v1' BEGIN SELECT RAISE(ABORT, 'test failure'); END");
        var intake = OrderTestData.Intake(store, payments.Client.BaseAddress!);
        await Assert.ThrowsAsync<SqliteException>(() => intake.ProcessAsync(message, CancellationToken.None));
        Assert.Equal(OrderStatus.Created, store.Get(message.OrderId)!.Status);
        Assert.Null(store.Get(message.OrderId)!.AuthorizationId);
        Assert.Equal(0, data.Scalar("Orders.Service", "SELECT COUNT(*) FROM outbox"));
        Assert.Equal(0, data.Scalar("Orders.Service", "SELECT processed FROM order_inbox"));
        var paid = await payments.Snapshot(message.OrderId);
        data.Execute("Orders.Service", "DROP TRIGGER test_fail");
        var restarted = data.NewOrders();
        var pending = Assert.Single(restarted.StartRecoveryCycle());
        Assert.True(await OrderTestData.Intake(restarted, payments.Client.BaseAddress!).ProcessAsync(pending, CancellationToken.None));
        Assert.Equal(paid.AuthorizationId, restarted.Get(message.OrderId)!.AuthorizationId);
        Assert.Equal(2, data.Scalar("Orders.Service", "SELECT COUNT(*) FROM outbox"));
        Assert.Equal(1, paymentData.Count("SELECT COUNT(*) FROM payments"));
    }

    [Fact]
    public async Task HTTP_timeout_retains_Created_and_recovers_original_authorization_after_restart()
    {
        using var data = new OrderTestData();
        using var paymentData = new PaymentData();
        var message = OrderTestData.Message();
        await using var payments = await PaymentHost.Start(paymentData, new("happy-path", 500, 0, message.OrderId));
        var store = data.NewOrders();
        Assert.False(await OrderTestData.Intake(store, payments.Client.BaseAddress!, TimeSpan.FromMilliseconds(80)).ProcessAsync(message, CancellationToken.None));
        var failed = store.Get(message.OrderId)!;
        Assert.Equal(OrderStatus.Created, failed.Status);
        Assert.Equal("payment_retry_exhausted", failed.LastError);
        Assert.Null(failed.FailureCode);
        Assert.Equal(3, data.Scalar("Orders.Service", "SELECT auth_attempts FROM orders"));
        var remote = await payments.Snapshot(message.OrderId);
        var restarted = data.NewOrders();
        Assert.Empty(restarted.StartRecoveryCycle());
        Assert.Equal(3, data.Scalar("Orders.Service", "SELECT auth_attempts FROM orders"));
        // An operator opens a new cycle only after correcting the cause.
        data.Execute("Orders.Service", "UPDATE orders SET auth_attempts = 0, last_error = NULL WHERE status = 'Created' AND authorization_blocked = 0");
        var pending = Assert.Single(restarted.StartRecoveryCycle());
        Assert.True(await OrderTestData.Intake(restarted, payments.Client.BaseAddress!).ProcessAsync(pending, CancellationToken.None));
        Assert.Equal(remote.AuthorizationId, restarted.Get(message.OrderId)!.AuthorizationId);
        Assert.Equal(OrderStatus.FulfillmentPending, restarted.Get(message.OrderId)!.Status);
        Assert.Null(restarted.Get(message.OrderId)!.LastError);
        Assert.Equal(1, paymentData.Count("SELECT COUNT(*) FROM payments"));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(409)]
    public async Task Permanent_Payments_errors_are_not_retried_or_turned_into_business_declines(int status)
    {
        using var data = new OrderTestData();
        var calls = 0;
        await using var remote = await OrderTestHost.Stub(app => app.MapPost("/api/v1/authorizations", () =>
        {
            Interlocked.Increment(ref calls);
            return Microsoft.AspNetCore.Http.Results.Json(new ApiError("invalid_request", "test"), WireJson.Options, statusCode: status);
        }));
        var store = data.NewOrders();
        var message = OrderTestData.Message();
        var intake = OrderTestData.Intake(store, remote.Client.BaseAddress!);
        Assert.False(await intake.ProcessAsync(message, CancellationToken.None));
        Assert.False(await intake.ProcessAsync(message, CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.Empty(store.StartRecoveryCycle());
        Assert.Equal(OrderStatus.Created, store.Get(message.OrderId)!.Status);
        Assert.Null(store.Get(message.OrderId)!.FailureCode);
        Assert.Equal("payments_request_rejected", store.Get(message.OrderId)!.LastError);
        Assert.Equal(0, data.Scalar("Orders.Service", "SELECT COUNT(*) FROM outbox"));
    }

    [Fact]
    public void AMQP_identity_and_encoding_are_validated_before_acquisition()
    {
        var message = OrderTestData.Message();
        var body = Encoding.UTF8.GetBytes(WireJson.Write(message));
        var properties = new BasicProperties { MessageId = message.MessageId.ToString(), Type = "OrderCreated.v1", ContentType = "application/json", Persistent = true };
        Assert.Equal(message.OrderId, OrderCreatedConsumer.Read(body, properties).OrderId);
        properties.MessageId = Guid.NewGuid().ToString();
        Assert.Throws<ArgumentException>(() => OrderCreatedConsumer.Read(body, properties));
        Assert.ThrowsAny<Exception>(() => OrderCreatedConsumer.Read(new byte[] { 0xff, 0xfe }, properties));
    }
}

internal sealed class OrderTestData : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "playground-orders-" + Guid.NewGuid().ToString("N"));
    public LabSettings Settings => new("orders-tests", Root, "127.0.0.1", 1, "unused", "unused");
    public OrdersStore NewOrders() { var store = new OrdersStore(Settings); store.Initialize(); return store; }
    public static CheckoutRequest Request() => new(Guid.NewGuid(), [new("BOOK", 2, 12.50m)], new("Ada", "Via Roma 1", "Bologna", "40100", "IT"));
    public static OrderCreated Message() { var request = Request(); return new(1, Guid.NewGuid(), DateTimeOffset.UtcNow, request.OrderId, request.Items, request.ShippingAddress); }
    public static OrderIntake Intake(OrdersStore store, Uri payments, TimeSpan? timeout = null) => new(store,
        new(new HttpClient { BaseAddress = payments, Timeout = timeout ?? TimeSpan.FromSeconds(5) }), NullLogger<OrderIntake>.Instance);
    public long Scalar(string service, string sql) => Convert.ToInt64(Query(Settings, service, sql));
    public void Execute(string service, string sql) => Query(Settings, service, sql);
    public static object? Query(LabSettings settings, string service, string sql)
    {
        using var connection = LocalStore.Open(settings.Database(service));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
    public void Dispose() => Clean(Settings);
    public static void Clean(LabSettings settings)
    {
        foreach (var service in new[] { "Checkout.Api", "Orders.Service" })
        {
            using var connection = LocalStore.Open(settings.Database(service));
            SqliteConnection.ClearPool(connection);
        }
        if (Directory.Exists(settings.Root)) Directory.Delete(settings.Root, recursive: true);
    }
}

internal sealed class OrderTestHost(WebApplication app, HttpClient client) : IAsyncDisposable
{
    public WebApplication App { get; } = app;
    public HttpClient Client { get; } = client;
    public static async Task<OrderTestHost> Start(WebApplication app)
    {
        await app.StartAsync();
        return new(app, new() { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) });
    }
    public static async Task<OrderTestHost> Stub(Action<WebApplication> routes)
    {
        var builder = Builder();
        var app = builder.Build();
        routes(app);
        return await Start(app);
    }
    public static async Task<OrderTestHost> Checkout(LabSettings settings, Uri orders, TimeSpan? timeout = null)
    {
        var builder = Builder();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<CheckoutStore>();
        builder.Services.AddSingleton(new OrdersLookup(new HttpClient { BaseAddress = orders, Timeout = timeout ?? TimeSpan.FromMilliseconds(300) }));
        var app = builder.Build();
        app.Services.GetRequiredService<CheckoutStore>().Initialize();
        app.MapCheckout();
        return await Start(app);
    }
    public static async Task<OrderTestHost> Orders(LabSettings settings)
    {
        var builder = Builder();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<OrdersStore>();
        var app = builder.Build();
        app.Services.GetRequiredService<OrdersStore>().Initialize();
        app.MapOrders();
        return await Start(app);
    }
    private static WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        return builder;
    }
    public async Task<OrderSnapshot> Snapshot(Guid orderId, bool internalRoute = false)
    {
        using var response = await Client.GetAsync($"/{(internalRoute ? "internal" : "api")}/v1/orders/{orderId}");
        response.EnsureSuccessStatusCode();
        return WireJson.Read<OrderSnapshot>(await response.Content.ReadAsStringAsync());
    }
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
