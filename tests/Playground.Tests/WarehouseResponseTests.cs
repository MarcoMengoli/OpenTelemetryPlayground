using Fulfillment.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Playground.Contracts;
using Playground.Infrastructure;
using Playground.WarehouseResponses;
using Xunit;
using Response = Playground.Contracts.WarehouseResponse;

namespace Playground.Tests;

public sealed class WarehouseResponseTests
{
    [Fact]
    public async Task Real_lookup_acquisition_restart_and_renamed_duplicates_have_one_stable_confirmation()
    {
        using var data = new ResponseData();
        var (response, order) = data.KnownResponse();
        await using var host = await ResponseLookupHost.Start(data.Settings);
        var source = data.Put("one.json", WireJson.Write(response));
        data.Reader().Scan(data.Now);
        Assert.True(File.Exists(source));
        Assert.Equal(0L, data.Query("SELECT COUNT(*) FROM outbox"));
        // Recreate service components over the same DB, before lookup/publish.
        var restarted = data.Store();
        await host.Lookup(restarted).ResumePendingAsync(data.Now, default);
        var original = new DurableOutbox(restarted.Database).Next()!;
        var confirmation = WireJson.Read<ShipmentConfirmation>(original.Body);
        Assert.Equal(order, confirmation.OrderId);
        Assert.Equal(response.ShipmentId, confirmation.ShipmentId);
        Assert.Equal(response.ShippedAt, confirmation.ShippedAt);
        data.Put("renamed.json", WireJson.Write(response));
        data.Put("other-offset.json", WireJson.Write(response with { ShippedAt = response.ShippedAt.ToOffset(TimeSpan.FromHours(2)) }));
        data.Reader().Scan(data.Now);
        Assert.Equal(3, data.Archived("processed").Length);
        Assert.Equal(original, new DurableOutbox(data.Store().Database).Next());
        Assert.Equal(1L, data.Query("SELECT COUNT(*) FROM responses"));
        Assert.Equal(1L, data.Query("SELECT COUNT(*) FROM outbox"));
    }

    [Fact]
    public async Task Durable_acquisition_survives_missing_source_file()
    {
        using var data = new ResponseData();
        var (response, _) = data.KnownResponse();
        var source = data.Put("response.json", WireJson.Write(response));
        data.Reader().Scan(data.Now);
        File.Delete(source);
        await using var host = await ResponseLookupHost.Start(data.Settings);
        await host.Lookup(data.Store()).ResumePendingAsync(data.Now, default);
        Assert.NotNull(new DurableOutbox(data.Store().Database).Next());
    }

    [Theory]
    [InlineData("shipment")]
    [InlineData("date")]
    public async Task Conflicting_business_response_is_quarantined_without_replacing_original(string change)
    {
        using var data = new ResponseData();
        var (response, _) = data.KnownResponse();
        data.Put("first.json", WireJson.Write(response));
        data.Reader().Scan(data.Now);
        await using var host = await ResponseLookupHost.Start(data.Settings);
        await host.Lookup(data.Store()).ResumePendingAsync(data.Now, default);
        var original = new DurableOutbox(data.Store().Database).Next();
        var conflict = change == "shipment" ? response with { ShipmentId = Guid.NewGuid() } : response with { ShippedAt = response.ShippedAt.AddSeconds(1) };
        data.Put("conflict.json", WireJson.Write(conflict));
        data.Reader().Scan(data.Now);
        Assert.Equal(WireJson.Write(conflict), WarehouseFiles.Read(Assert.Single(data.Archived("errors"))));
        Assert.Equal(original, new DurableOutbox(data.Store().Database).Next());
    }

    [Fact]
    public async Task Malformed_invalid_utf8_and_unknown_fields_do_not_block_valid_files_or_consume_temporary_files()
    {
        using var data = new ResponseData();
        var (response, _) = data.KnownResponse();
        data.Put("a-invalid.json", "{");
        File.WriteAllBytes(data.Put("b-invalid.json", ""), [0xff]);
        data.Put("c-extra.json", WireJson.Write(response).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"orderId\":\"forbidden\""));
        data.Put("z-valid.json", WireJson.Write(response));
        var temp = data.Put("unfinished.json.tmp-123", "{");
        data.Reader().Scan(data.Now);
        await using var host = await ResponseLookupHost.Start(data.Settings);
        await host.Lookup(data.Store()).ResumePendingAsync(data.Now, default);
        data.Reader().Scan(data.Now);
        Assert.Equal(3, data.Archived("errors").Length);
        Assert.Single(data.Archived("processed"));
        Assert.True(File.Exists(temp));
        Assert.Equal(1L, data.Query("SELECT COUNT(*) FROM outbox"));
    }

    [Fact]
    public async Task Unknown_reference_exhausts_durable_budget_and_is_quarantined()
    {
        using var data = new ResponseData();
        var response = new Response(1, "unknown", Guid.NewGuid(), data.Now);
        data.Put("unknown.json", WireJson.Write(response));
        data.Reader().Scan(data.Now);
        await using var host = await ResponseLookupHost.Start(data.Settings);
        for (var i = 0; i < 3; i++)
            await host.Lookup(data.Store()).ResumePendingAsync(data.Now.AddSeconds(10 * i), default);
        data.Reader().Scan(data.Now.AddMinutes(1));
        var acquired = data.Store().Get(response.WarehouseReference)!;
        Assert.Equal("Rejected", acquired.State);
        Assert.Equal(3, acquired.Attempts);
        Assert.Equal("lookup_http_404", acquired.LastError);
        Assert.Single(data.Archived("errors"));
        Assert.Equal(0L, data.Query("SELECT COUNT(*) FROM outbox"));
        await host.Lookup(data.Store()).ResumePendingAsync(data.Now.AddHours(1), default);
        Assert.Equal(3, data.Store().Get(response.WarehouseReference)!.Attempts);
    }

    [Fact]
    public async Task Temporary_http_failure_obeys_delay_and_recovers_after_consumer_restart()
    {
        using var data = new ResponseData();
        var (response, _) = data.KnownResponse();
        data.Put("response.json", WireJson.Write(response));
        data.Reader().Scan(data.Now);
        await using var host = await ResponseLookupHost.Start(data.Settings, failures: 1);
        await host.Lookup(data.Store()).ResumePendingAsync(data.Now, default);
        Assert.Equal("lookup_http_503", data.Store().Get(response.WarehouseReference)!.LastError);
        await host.Lookup(data.Store()).ResumePendingAsync(data.Now, default);
        Assert.Equal(1, host.Calls);
        await host.Lookup(data.Store()).ResumePendingAsync(data.Now.AddSeconds(1), default);
        Assert.Equal(2, host.Calls);
        Assert.Equal("Ready", data.Store().Get(response.WarehouseReference)!.State);
        Assert.Equal(1L, data.Query("SELECT COUNT(*) FROM outbox"));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("mismatch")]
    [InlineData("conflict")]
    public async Task Invalid_lookup_responses_are_permanent_and_never_invent_an_order(string mode)
    {
        using var data = new ResponseData();
        var (response, _) = data.KnownResponse();
        data.Put("response.json", WireJson.Write(response));
        data.Reader().Scan(data.Now);
        await using var host = await ResponseLookupHost.Start(data.Settings, mode: mode);
        await host.Lookup(data.Store()).ResumePendingAsync(data.Now, default);
        await host.Lookup(data.Store()).ResumePendingAsync(data.Now.AddHours(1), default);
        data.Reader().Scan(data.Now);
        Assert.Equal(1, host.Calls);
        Assert.Single(data.Archived("errors"));
        Assert.Equal(0L, data.Query("SELECT COUNT(*) FROM outbox"));
    }

    [Fact]
    public async Task Crash_during_final_lookup_attempt_does_not_reset_budget_on_restart()
    {
        using var data = new ResponseData();
        var (response, _) = data.KnownResponse();
        var store = data.Store();
        store.Accept(response);
        for (var i = 0; i < 3; i++) store.StartAttempt(response.WarehouseReference, data.Now);
        await using var host = await ResponseLookupHost.Start(data.Settings);
        await host.Lookup(data.Store()).ResumePendingAsync(data.Now.AddMinutes(1), default);
        Assert.Equal(0, host.Calls);
        Assert.Equal("Rejected", data.Store().Get(response.WarehouseReference)!.State);
        Assert.Equal("lookup_retry_exhausted", data.Store().Get(response.WarehouseReference)!.LastError);
    }

    [Fact]
    public void Lookup_and_outbox_commit_atomically_and_reject_two_references_for_one_order()
    {
        using var data = new ResponseData();
        var (response, order) = data.KnownResponse();
        var store = data.Store();
        store.Accept(response);
        data.Query("CREATE TRIGGER fail_ready BEFORE UPDATE OF state ON responses WHEN NEW.state='Ready' BEGIN SELECT RAISE(ABORT,'test interruption'); END");
        Assert.Throws<SqliteException>(() => store.Complete(response, new(response.WarehouseReference, order)));
        Assert.Equal(0L, data.Query("SELECT COUNT(*) FROM outbox"));
        Assert.Equal("PendingLookup", store.Get(response.WarehouseReference)!.State);
        data.Query("DROP TRIGGER fail_ready");
        store.Complete(response, new(response.WarehouseReference, order));
        var other = response with { WarehouseReference = "other" };
        store.Accept(other);
        Assert.Throws<ResponseConflictException>(() => store.Complete(other, new(other.WarehouseReference, order)));
        Assert.Equal(1L, data.Query("SELECT COUNT(*) FROM outbox"));
    }

    [Fact]
    public void Archive_failure_is_bounded_durable_and_recoverable_without_new_confirmation()
    {
        using var data = new ResponseData();
        var (response, order) = data.KnownResponse();
        var source = data.Put("response.json", WireJson.Write(response));
        var store = data.Store();
        store.Accept(response);
        store.Complete(response, new(response.WarehouseReference, order));
        var original = new DurableOutbox(store.Database).Next();
        var obstruction = Path.Combine(data.Settings.Warehouse, "processed");
        File.WriteAllText(obstruction, "blocks archive directory");
        for (var i = 0; i < 3; i++) data.Reader().Scan(data.Now.AddSeconds(i * 10));
        Assert.True(File.Exists(source));
        Assert.Equal(3L, data.Query("SELECT attempts FROM file_failures"));
        File.Delete(obstruction);
        data.Reader().Scan(data.Now.AddHours(1));
        Assert.True(File.Exists(source)); // Restart alone cannot reset an exhausted budget.
        data.Store().ClearFileFailure("response.json"); // Explicit operator repair.
        data.Reader().Scan(data.Now.AddHours(1));
        Assert.False(File.Exists(source));
        Assert.Single(data.Archived("processed"));
        Assert.Equal(original, new DurableOutbox(store.Database).Next());
    }
}

internal sealed class ResponseData : IDisposable
{
    public LabSettings Settings { get; }
    public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
    public ResponseData(LabSettings? settings = null)
    {
        Settings = settings ?? new("us09-test", Path.Combine(Path.GetTempPath(), "playground-us09-" + Guid.NewGuid().ToString("N")), "127.0.0.1", 18700, "unused", "unused");
        Store();
    }
    public ResponseStore Store() { var store = new ResponseStore(Settings); store.Initialize(); return store; }
    public ResponseReader Reader() => new(Settings, Store(), NullLogger<ResponseReader>.Instance);
    public (Response, Guid) KnownResponse()
    {
        var fulfillment = new FulfillmentStore(Settings);
        fulfillment.Initialize();
        var message = FulfillmentTests.Message();
        fulfillment.Accept(message);
        fulfillment.Complete(message.OrderId, new(message.OrderId, "SIM", 2));
        var request = WireJson.Read<WarehouseRequest>(fulfillment.UnsentFiles().Single(x => x.WarehouseReference == fulfillment.Get(message.OrderId)!.WarehouseReference).PayloadJson);
        return (new(1, request.WarehouseReference, Guid.NewGuid(), Now), message.OrderId);
    }
    public string Put(string name, string json)
    {
        var directory = Path.Combine(Settings.Warehouse, "responses");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, json);
        return path;
    }
    public string[] Archived(string kind)
    {
        var directory = Path.Combine(Settings.Warehouse, kind, "responses");
        return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json") : [];
    }
    public object? Query(string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Store().Database, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
    public void Dispose()
    {
        using var pooled = LocalStore.Open(Settings.Database("WarehouseResponse.Service"));
        SqliteConnection.ClearPool(pooled);
        if (Directory.Exists(Settings.Root)) Directory.Delete(Settings.Root, recursive: true);
    }
}

internal sealed class ResponseLookupHost(WebApplication app, HttpClient client) : IAsyncDisposable
{
    private readonly HttpClient http = client;
    public int Calls { get; private set; }
    public ResponseLookup Lookup(ResponseStore store) => new(store, http, NullLogger<ResponseLookup>.Instance);
    public static async Task<ResponseLookupHost> Start(LabSettings settings, int failures = 0, string? mode = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new FulfillmentStore(settings));
        var app = builder.Build();
        app.Services.GetRequiredService<FulfillmentStore>().Initialize();
        var host = new ResponseLookupHost(app, new HttpClient { Timeout = TimeSpan.FromSeconds(5) });
        app.Use(async (context, next) =>
        {
            host.Calls++;
            if (host.Calls <= failures) { context.Response.StatusCode = 503; return; }
            if (mode == "conflict") { context.Response.StatusCode = 409; return; }
            if (mode == "malformed") { await context.Response.WriteAsync("{}"); return; }
            if (mode == "mismatch") { await context.Response.WriteAsync(WireJson.Write(new WarehouseMappingResponse("wrong", Guid.NewGuid()))); return; }
            await next(context);
        });
        app.MapFulfillment();
        await app.StartAsync();
        host.http.BaseAddress = new Uri(app.Urls.Single());
        return host;
    }
    public async ValueTask DisposeAsync() { http.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
}
