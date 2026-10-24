using System.Net;
using System.Text;
using System.Text.Json;
using Couriers.Api;
using Fulfillment.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Playground.Contracts;
using Playground.Infrastructure;
using RabbitMQ.Client;
using Xunit;

namespace Playground.Tests;

public sealed class FulfillmentTests
{
    [Fact]
    public async Task Courier_http_is_deterministic_concurrent_validated_and_durable()
    {
        using var data = new FulfillmentData();
        var message = Message();
        var request = new DeliveryEstimateRequest(message.OrderId, message.ShippingAddress);
        DeliveryEstimateResponse first;
        await using (var host = await CourierHost.Start(data))
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => host.Post(request)));
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            var estimates = await Task.WhenAll(responses.Select(async r => WireJson.Read<DeliveryEstimateResponse>(await r.Content.ReadAsStringAsync())));
            first = Assert.Single(estimates.Distinct());
            foreach (var response in responses) response.Dispose();
            Assert.Equal(2, first.EstimatedDays);
            using var foreign = await host.Post(request with { OrderId = Guid.NewGuid(), ShippingAddress = request.ShippingAddress with { CountryCode = "FR" } });
            Assert.Equal(5, WireJson.Read<DeliveryEstimateResponse>(await foreign.Content.ReadAsStringAsync()).EstimatedDays);
            using var conflict = await host.Post(request with { ShippingAddress = request.ShippingAddress with { City = "Roma" } });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            foreach (var json in new[] { "{}", "null", "{", WireJson.Write(request).Insert(1, "\"unknown\":1,") })
            {
                using var invalid = await host.Client.PostAsync("/api/v1/estimates", new StringContent(json, Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                Assert.Equal("invalid_request", WireJson.Read<ApiError>(await invalid.Content.ReadAsStringAsync()).Code);
            }
        }
        await using var restarted = await CourierHost.Start(data);
        using var repeated = await restarted.Post(request);
        Assert.Equal(first, WireJson.Read<DeliveryEstimateResponse>(await repeated.Content.ReadAsStringAsync()));
        Assert.Equal(2, data.Count("Couriers.Api", "SELECT COUNT(*) FROM courier_estimates"));
    }

    [Fact]
    public async Task Courier_failure_counter_and_order_selector_survive_restart()
    {
        using var data = new FulfillmentData();
        var message = Message();
        var request = new DeliveryEstimateRequest(message.OrderId, message.ShippingAddress);
        var simulation = new CourierSimulation("temporary-courier-failure", 0, 2, request.OrderId);
        await using (var first = await CourierHost.Start(data, simulation))
        {
            using var failed = await first.Post(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            using var other = await first.Post(request with { OrderId = Guid.NewGuid() });
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        }
        await using var second = await CourierHost.Start(data, simulation);
        using var stillFailed = await second.Post(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, stillFailed.StatusCode);
        using var success = await second.Post(request);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(3, data.CourierAttempts(request.OrderId));
    }

    [Fact]
    public async Task Inbox_order_identity_mapping_and_file_outbox_survive_restart_without_writing_files()
    {
        using var data = new FulfillmentData();
        var message = Message();
        var store = data.Store();
        store.Accept(message);
        store.Accept(message);
        store.Accept(message with { Items = [message.Items[0] with { UnitPrice = 12.500m }] });
        store.Accept(message with { MessageId = Guid.NewGuid(), OccurredAt = message.OccurredAt.AddSeconds(1) });
        Assert.Throws<FulfillmentConflictException>(() => store.Accept(message with { OrderId = Guid.NewGuid() }));
        Assert.Throws<FulfillmentConflictException>(() => store.Accept(message with { OccurredAt = message.OccurredAt.AddSeconds(1) }));
        Assert.Throws<FulfillmentConflictException>(() => store.Accept(message with { MessageId = Guid.NewGuid(), Items = [new("OTHER", 2, 5)] }));
        var reference = store.Get(message.OrderId)!.WarehouseReference;
        await using var host = await CourierHost.Start(data);
        Assert.True(await host.Processor(store).ProcessAsync(message.OrderId, CancellationToken.None));
        store = data.Store();
        store.Accept(message);
        Assert.True(await host.Processor(store).ProcessAsync(message.OrderId, CancellationToken.None));
        var file = Assert.Single(store.UnsentFiles());
        var payload = WireJson.Read<WarehouseRequest>(file.PayloadJson);
        Assert.Equal(reference, payload.WarehouseReference);
        Assert.Equal(message.Items, payload.Items);
        Assert.Equal("Ready", store.Get(message.OrderId)!.State);
        Assert.Null(store.Get(message.OrderId)!.LastError);
        Assert.Equal(1, data.CourierAttempts(message.OrderId));
        Assert.Equal(2, data.Count("Fulfillment.Service", "SELECT COUNT(*) FROM fulfillment_inbox"));
        Assert.Equal(new WarehouseMappingResponse(reference, message.OrderId), store.Lookup(reference));
        using var mapping = await host.Client.GetAsync("/internal/v1/warehouse-requests/" + reference);
        Assert.Equal(new WarehouseMappingResponse(reference, message.OrderId), WireJson.Read<WarehouseMappingResponse>(await mapping.Content.ReadAsStringAsync()));
        using var unknown = await host.Client.GetAsync("/internal/v1/warehouse-requests/unknown");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.False(Directory.Exists(data.Settings.Warehouse));
        using var document = JsonDocument.Parse(file.PayloadJson);
        Assert.Equal(new[] { "courierCode", "estimatedDays", "items", "schemaVersion", "shippingAddress", "warehouseReference" },
            document.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task Transient_failures_use_three_durable_attempts_and_never_invent_success(int failures, bool ready)
    {
        using var data = new FulfillmentData();
        var message = Message();
        var store = data.Store();
        store.Accept(message);
        await using var host = await CourierHost.Start(data, new("temporary-courier-failure", 0, failures, null));
        Assert.Equal(ready, await host.Processor(store).ProcessAsync(message.OrderId, CancellationToken.None));
        Assert.Equal(3, store.Get(message.OrderId)!.Attempts);
        Assert.Equal(3, data.CourierAttempts(message.OrderId));
        var reopened = data.Store();
        await host.Processor(reopened).ResumePendingAsync(CancellationToken.None);
        Assert.Equal(3, data.CourierAttempts(message.OrderId));
        Assert.Equal(ready ? "Ready" : "Failed", reopened.Get(message.OrderId)!.State);
        Assert.Equal(ready ? 1 : 0, reopened.UnsentFiles().Count);
        Assert.Equal(ready ? null : "courier_http_503", reopened.Get(message.OrderId)!.LastError);
    }

    [Fact]
    public async Task Timeout_is_bounded_and_pending_call_recovers_with_same_identity()
    {
        using var data = new FulfillmentData();
        var message = Message();
        var store = data.Store();
        store.Accept(message);
        var reference = store.Get(message.OrderId)!.WarehouseReference;
        await using (var slow = await CourierHost.Start(data, new("slow-courier", 6000, 0, message.OrderId)))
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var processing = slow.Processor(store).ProcessAsync(message.OrderId, stop.Token);
            await Until(() => store.Get(message.OrderId) is { Attempts: 1, LastError: "courier_timeout" });
            // Stop after an actual five-second timeout, before the second attempt.
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
            Assert.Empty(store.UnsentFiles());
            Assert.Equal("Pending", store.Get(message.OrderId)!.State);
        }
        await using var restarted = await CourierHost.Start(data);
        store = data.Store();
        await restarted.Processor(store).ResumePendingAsync(CancellationToken.None);
        Assert.Equal("Ready", store.Get(message.OrderId)!.State);
        Assert.Equal(2, store.Get(message.OrderId)!.Attempts);
        Assert.Equal(reference, Assert.Single(store.UnsentFiles()).WarehouseReference);
        Assert.Equal(2, data.CourierAttempts(message.OrderId));
    }

    [Fact]
    public async Task Arrest_after_last_attempt_reservation_does_not_reset_retry_budget()
    {
        using var data = new FulfillmentData();
        var store = data.Store();
        var message = Message();
        store.Accept(message);
        Assert.Equal(1, store.BeginAttempt(message.OrderId));
        Assert.Equal(2, store.BeginAttempt(message.OrderId));
        Assert.Equal(3, store.BeginAttempt(message.OrderId));
        await using var host = await CourierHost.Start(data);
        var reopened = data.Store();
        await host.Processor(reopened).ResumePendingAsync(CancellationToken.None);
        Assert.Equal("Failed", reopened.Get(message.OrderId)!.State);
        Assert.Equal("retry_exhausted", reopened.Get(message.OrderId)!.LastError);
        Assert.Empty(reopened.UnsentFiles());
        Assert.Equal(0, data.CourierAttempts(message.OrderId));
    }

    [Theory]
    [InlineData("mismatch", "courier_order_mismatch")]
    [InlineData("malformed", "courier_invalid_response")]
    [InlineData("conflict", "courier_http_409")]
    public async Task Permanent_courier_errors_stop_after_one_attempt(string mode, string error)
    {
        using var data = new FulfillmentData();
        var store = data.Store();
        var message = Message();
        store.Accept(message);
        await using var host = await CourierHost.Start(data, brokenResponse: mode);
        Assert.False(await host.Processor(store).ProcessAsync(message.OrderId, CancellationToken.None));
        Assert.Equal(1, store.Get(message.OrderId)!.Attempts);
        Assert.Equal(error, store.Get(message.OrderId)!.LastError);
        Assert.Empty(store.UnsentFiles());
    }

    internal static FulfillOrder Message() => new(1, Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(),
        [new("BOOK", 1, 12.50m)], new("Ada", "Via Roma 1", "Bologna", "40100", "IT"));
    internal static async Task Until(Func<bool> condition, int seconds = 30)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }
}

internal sealed class FulfillmentData : IDisposable
{
    public LabSettings Settings { get; } = new("test-b", Path.Combine(Path.GetTempPath(), "playground-us07-" + Guid.NewGuid().ToString("N")), "127.0.0.1", 18500, "unused", "unused");
    public FulfillmentStore Store()
    {
        var store = new FulfillmentStore(Settings);
        store.Initialize();
        return store;
    }
    public long Count(string service, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Settings.Database(service), Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
    public long CourierAttempts(Guid id) => Count("Couriers.Api", $"SELECT COALESCE(MAX(attempts),0) FROM courier_estimates WHERE order_id='{id}'");
    public void Dispose()
    {
        // This unique root is created by this fixture alone, never a running lab.
        if (Directory.Exists(Settings.Root)) Directory.Delete(Settings.Root, recursive: true);
    }
}

internal sealed class CourierHost(WebApplication app, HttpClient client) : IAsyncDisposable
{
    public HttpClient Client { get; } = client;
    public FulfillmentProcessor Processor(FulfillmentStore store) => new(store, Client, NullLogger<FulfillmentProcessor>.Instance);
    public Task<HttpResponseMessage> Post(DeliveryEstimateRequest request) =>
        Client.PostAsync("/api/v1/estimates", new StringContent(WireJson.Write(request), Encoding.UTF8, "application/json"));
    public static async Task<CourierHost> Start(FulfillmentData data, CourierSimulation? simulation = null, string? brokenResponse = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(data.Settings);
        builder.Services.AddSingleton(simulation ?? new("happy-path", 0, 0, null));
        builder.Services.AddSingleton<CourierStore>();
        builder.Services.AddSingleton<FulfillmentStore>();
        var app = builder.Build();
        app.Services.GetRequiredService<CourierStore>().Initialize();
        app.Services.GetRequiredService<FulfillmentStore>().Initialize();
        if (brokenResponse is null) app.MapCouriers();
        else app.MapPost("/api/v1/estimates", () => brokenResponse switch
        {
            "mismatch" => Microsoft.AspNetCore.Http.Results.Json(new DeliveryEstimateResponse(Guid.NewGuid(), "SIM", 2), WireJson.Options),
            "conflict" => Microsoft.AspNetCore.Http.Results.Json(new ApiError("idempotency_conflict", "Conflict"), WireJson.Options, statusCode: 409),
            _ => Microsoft.AspNetCore.Http.Results.Text("{}")
        });
        app.MapFulfillment();
        await app.StartAsync();
        return new(app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(5) });
    }
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}