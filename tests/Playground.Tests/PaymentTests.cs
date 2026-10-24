using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Payments.Api;
using Playground.Contracts;
using Playground.Infrastructure;
using Xunit;

namespace Playground.Tests;

// Real HTTP over Kestrel and the service's real SQLite store. No broker is needed by
// these handlers; full host readiness and the real broker are covered by lab verify.
public sealed class PaymentTests
{
    [Fact]
    public async Task Concurrent_duplicates_have_one_authorization_capture_and_invoice()
    {
        using var data = new PaymentData();
        await using var host = await PaymentHost.Start(data);
        var order = Guid.NewGuid();
        var authorize = new AuthorizePaymentRequest(order, new(12.50m, "EUR"));
        var authorizations = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => host.Post<AuthorizationResponse>("authorizations", authorize)));
        var authorization = Assert.Single(authorizations.Distinct());
        Assert.Equal(PaymentStatus.Authorized, (await host.Snapshot(order)).Status);
        var capture = new CapturePaymentRequest(order, authorization.AuthorizationId!.Value);
        var captures = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => host.Post<CapturePaymentResponse>("captures", capture)));
        var captured = Assert.Single(captures.Distinct());
        var afterCapture = await host.Snapshot(order);
        Assert.Equal(PaymentStatus.Captured, afterCapture.Status);
        Assert.Null(afterCapture.InvoiceId);
        var invoice = new IssueInvoiceRequest(order, captured.CaptureId);
        var invoices = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => host.Post<InvoiceResponse>("invoices", invoice)));
        var invoiced = Assert.Single(invoices.Distinct());
        Assert.Equal(authorize.Total, invoiced.Total);
        Assert.Equal(authorization, await host.Post<AuthorizationResponse>("authorizations", authorize with { Total = new(12.5m, "EUR") }));
        Assert.Equal(captured, await host.Post<CapturePaymentResponse>("captures", capture));
        Assert.Equal(invoiced, await host.Post<InvoiceResponse>("invoices", invoice));
        await host.Error("authorization-cancellations", new CancelAuthorizationRequest(order, capture.AuthorizationId), HttpStatusCode.Conflict, "invalid_state");
        Assert.Equal(PaymentStatus.Invoiced, (await host.Snapshot(order)).Status);
        Assert.Equal(1, data.Count("SELECT COUNT(*) FROM payments WHERE authorization_id IS NOT NULL AND capture_id IS NOT NULL AND invoice_id IS NOT NULL"));
        Assert.Equal(3, data.Count("SELECT COUNT(*) FROM payment_attempts WHERE attempts = 1"));
    }

    [Fact]
    public async Task Cancellation_is_idempotent_and_prevents_capture()
    {
        using var data = new PaymentData();
        await using var host = await PaymentHost.Start(data);
        var order = Guid.NewGuid();
        var request = new AuthorizePaymentRequest(order, new(8, "EUR"));
        var authorization = await host.Post<AuthorizationResponse>("authorizations", request);
        var cancel = new CancelAuthorizationRequest(order, authorization.AuthorizationId!.Value);
        var cancellations = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => host.Post<CancelAuthorizationResponse>("authorization-cancellations", cancel)));
        Assert.Single(cancellations.Distinct());
        await host.Error("captures", new CapturePaymentRequest(order, cancel.AuthorizationId), HttpStatusCode.Conflict, "invalid_state");
        Assert.Equal(authorization, await host.Post<AuthorizationResponse>("authorizations", request));
        Assert.Equal(PaymentStatus.Cancelled, (await host.Snapshot(order)).Status);
        Assert.Equal(0, data.Count("SELECT COUNT(*) FROM payments WHERE capture_id IS NOT NULL OR invoice_id IS NOT NULL"));
    }

    [Fact]
    public async Task Business_decline_and_conflicting_amount_survive_restart()
    {
        using var data = new PaymentData();
        var order = Guid.NewGuid();
        var request = new AuthorizePaymentRequest(order, new(8, "EUR"));
        AuthorizationResponse declined;
        await using (var first = await PaymentHost.Start(data, new("payment-declined", 0, 0, order)))
        {
            declined = await first.Post<AuthorizationResponse>("authorizations", request);
            Assert.Equal(PaymentDecision.Declined, declined.Decision);
            Assert.Null(declined.AuthorizationId);
            // The selector leaves a different order unaffected.
            var other = await first.Post<AuthorizationResponse>("authorizations", request with { OrderId = Guid.NewGuid() });
            Assert.Equal(PaymentDecision.Authorized, other.Decision);
        }
        await using var second = await PaymentHost.Start(data);
        Assert.Equal(declined, await second.Post<AuthorizationResponse>("authorizations", request));
        await second.Error("authorizations", request with { Total = new(9, "EUR") }, HttpStatusCode.Conflict, "idempotency_conflict");
        await second.Error("captures", new CapturePaymentRequest(order, Guid.NewGuid()), HttpStatusCode.Conflict, "invalid_state");
        var snapshot = await second.Snapshot(order);
        Assert.Equal(PaymentStatus.Declined, snapshot.Status);
        Assert.Null(snapshot.AuthorizationId);
        Assert.Null(snapshot.CaptureId);
        Assert.Null(snapshot.InvoiceId);
    }

    [Fact]
    public async Task References_cannot_be_reused_for_another_order_or_in_the_wrong_state()
    {
        using var data = new PaymentData();
        await using var host = await PaymentHost.Start(data);
        var order = Guid.NewGuid();
        var other = Guid.NewGuid();
        var request = new AuthorizePaymentRequest(order, new(6, "EUR"));
        var authorization = await host.Post<AuthorizationResponse>("authorizations", request);
        var auth = authorization.AuthorizationId!.Value;
        await host.Error("authorizations", request with { Total = new(7, "EUR") }, HttpStatusCode.Conflict, "idempotency_conflict");
        await host.Error("captures", new CapturePaymentRequest(other, auth), HttpStatusCode.Conflict, "idempotency_conflict");
        await host.Error("authorization-cancellations", new CancelAuthorizationRequest(other, auth), HttpStatusCode.Conflict, "idempotency_conflict");
        await host.Error("captures", new CapturePaymentRequest(order, Guid.NewGuid()), HttpStatusCode.Conflict, "invalid_state");
        await host.Error("invoices", new IssueInvoiceRequest(order, Guid.NewGuid()), HttpStatusCode.Conflict, "invalid_state");
        var captured = await host.Post<CapturePaymentResponse>("captures", new CapturePaymentRequest(order, auth));
        await host.Error("invoices", new IssueInvoiceRequest(other, captured.CaptureId), HttpStatusCode.Conflict, "idempotency_conflict");
        await host.Error("authorization-cancellations", new CancelAuthorizationRequest(order, auth), HttpStatusCode.Conflict, "invalid_state");
        Assert.Equal(PaymentStatus.Captured, (await host.Snapshot(order)).Status);
        using var missing = await host.Client.GetAsync($"/api/v1/payments/{other}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not_found", WireJson.Read<ApiError>(await missing.Content.ReadAsStringAsync()).Code);
        foreach (var id in new[] { "invalid", Guid.Empty.ToString() })
        {
            using var invalid = await host.Client.GetAsync($"/api/v1/payments/{id}");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("invalid_request", WireJson.Read<ApiError>(await invalid.Content.ReadAsStringAsync()).Code);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("{\"orderId\":\"00000000-0000-0000-0000-000000000000\",\"total\":{\"amount\":1,\"currency\":\"EUR\"}}")]
    [InlineData("{\"orderId\":\"11111111-1111-4111-8111-111111111111\",\"total\":null}")]
    [InlineData("{\"orderId\":\"11111111-1111-4111-8111-111111111111\",\"total\":{\"amount\":0,\"currency\":\"EUR\"}}")]
    [InlineData("{\"orderId\":\"11111111-1111-4111-8111-111111111111\",\"total\":{\"amount\":-1,\"currency\":\"EUR\"}}")]
    [InlineData("{\"orderId\":\"11111111-1111-4111-8111-111111111111\",\"total\":{\"amount\":1.001,\"currency\":\"EUR\"}}")]
    [InlineData("{\"orderId\":\"11111111-1111-4111-8111-111111111111\",\"total\":{\"amount\":1,\"currency\":\"USD\"}}")]
    [InlineData("{\"orderId\":\"11111111-1111-4111-8111-111111111111\",\"total\":{\"amount\":1,\"currency\":\"EUR\"},\"extra\":true}")]
    public async Task Invalid_wire_payloads_return_the_uniform_error_without_effects(string json)
    {
        using var data = new PaymentData();
        await using var host = await PaymentHost.Start(data);
        using var response = await host.Client.PostAsync("/api/v1/authorizations", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", WireJson.Read<ApiError>(await response.Content.ReadAsStringAsync()).Code);
        Assert.Equal(0, data.Count("SELECT COUNT(*) FROM payments"));
        Assert.Equal(0, data.Count("SELECT COUNT(*) FROM payment_attempts"));
    }

    [Theory]
    [InlineData("authorizations")]
    [InlineData("captures")]
    [InlineData("invoices")]
    [InlineData("authorization-cancellations")]
    public async Task Temporary_failure_budget_is_durable_and_replays_never_fail_again(string operation)
    {
        using var data = new PaymentData();
        var order = Guid.NewGuid();
        object request;
        PaymentStatus? before;
        await using (var setup = await PaymentHost.Start(data)) (request, before) = await Prepare(setup, operation, order);
        var simulation = new PaymentSimulation("temporary-payment-failure", 0, 2, order);
        await using (var first = await PaymentHost.Start(data, simulation))
        {
            await first.Error(operation, request, HttpStatusCode.ServiceUnavailable, "temporarily_unavailable");
            if (operation == "authorizations")
                await first.Error(operation, new AuthorizePaymentRequest(order, new(10, "EUR")), HttpStatusCode.Conflict, "idempotency_conflict");
            if (before.HasValue) Assert.Equal(before, (await first.Snapshot(order)).Status);
            else
            {
                using var unknown = await first.Client.GetAsync($"/api/v1/payments/{order}");
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            }
        }
        await using var restarted = await PaymentHost.Start(data, simulation);
        await restarted.Error(operation, request, HttpStatusCode.ServiceUnavailable, "temporarily_unavailable");
        using var success = await restarted.Client.PostAsJsonAsync("/api/v1/" + operation, request, WireJson.Options);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        var result = await success.Content.ReadAsStringAsync();
        using var replay = await restarted.Client.PostAsJsonAsync("/api/v1/" + operation, request, WireJson.Options);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(result, await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, data.Count("SELECT COUNT(*) FROM payment_attempts WHERE attempts = 3"));
    }

    [Theory]
    [InlineData("authorizations", PaymentStatus.Authorized)]
    [InlineData("captures", PaymentStatus.Captured)]
    [InlineData("invoices", PaymentStatus.Invoiced)]
    [InlineData("authorization-cancellations", PaymentStatus.Cancelled)]
    public async Task Lost_HTTP_response_after_commit_is_recovered_with_same_identity_after_restart(string operation, PaymentStatus expected)
    {
        using var data = new PaymentData();
        var order = Guid.NewGuid();
        object request;
        await using (var setup = await PaymentHost.Start(data)) (request, _) = await Prepare(setup, operation, order);
        PaymentSnapshot persisted;
        await using (var slow = await PaymentHost.Start(data, new("happy-path", 10000, 0, order)))
        {
            using var aborted = new CancellationTokenSource();
            var lost = slow.Client.PostAsJsonAsync("/api/v1/" + operation, request, WireJson.Options, aborted.Token);
            persisted = await WaitForEffect(slow, order, expected);
            Assert.False(lost.IsCompleted);
            aborted.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var ignored = await lost; });
        }
        await using var restarted = await PaymentHost.Start(data);
        Assert.Equal(persisted, await restarted.Snapshot(order));
        using var replay = await restarted.Client.PostAsJsonAsync("/api/v1/" + operation, request, WireJson.Options);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(persisted, await restarted.Snapshot(order));
        var json = await replay.Content.ReadAsStringAsync();
        switch (operation)
        {
            case "authorizations": Assert.Equal(persisted.AuthorizationId, WireJson.Read<AuthorizationResponse>(json).AuthorizationId); break;
            case "captures": Assert.Equal(persisted.CaptureId, WireJson.Read<CapturePaymentResponse>(json).CaptureId); break;
            case "invoices": Assert.Equal(persisted.InvoiceId, WireJson.Read<InvoiceResponse>(json).InvoiceId); break;
            default: Assert.Equal(persisted.AuthorizationId, WireJson.Read<CancelAuthorizationResponse>(json).AuthorizationId); break;
        }
    }

    [Fact]
    public async Task Competing_capture_and_cancellation_commit_only_one_transition()
    {
        using var data = new PaymentData();
        await using var host = await PaymentHost.Start(data);
        var order = Guid.NewGuid();
        var authorization = await host.Post<AuthorizationResponse>("authorizations", new AuthorizePaymentRequest(order, new(8, "EUR")));
        var id = authorization.AuthorizationId!.Value;
        var responses = await Task.WhenAll(
            host.Client.PostAsJsonAsync("/api/v1/captures", new CapturePaymentRequest(order, id), WireJson.Options),
            host.Client.PostAsJsonAsync("/api/v1/authorization-cancellations", new CancelAuthorizationRequest(order, id), WireJson.Options));
        try
        {
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
            var payment = await host.Snapshot(order);
            Assert.Contains(payment.Status, new[] { PaymentStatus.Captured, PaymentStatus.Cancelled });
            Assert.Equal(payment.Status == PaymentStatus.Captured, payment.CaptureId.HasValue);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Theory]
    [InlineData("Simulation:Profile", "unknown")]
    [InlineData("Simulation:DelayMs", "-1")]
    [InlineData("Simulation:FailuresBeforeSuccess", "-1")]
    [InlineData("Simulation:OrderId", "invalid")]
    public void Invalid_simulation_configuration_fails_at_startup(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        Assert.Throws<ArgumentException>(() => PaymentSimulation.Read(config));
    }

    private static async Task<(object Request, PaymentStatus? Before)> Prepare(PaymentHost host, string operation, Guid order)
    {
        var authorize = new AuthorizePaymentRequest(order, new(8, "EUR"));
        if (operation == "authorizations") return (authorize, null);
        var authorization = await host.Post<AuthorizationResponse>("authorizations", authorize);
        var id = authorization.AuthorizationId!.Value;
        if (operation == "captures") return (new CapturePaymentRequest(order, id), PaymentStatus.Authorized);
        if (operation == "authorization-cancellations") return (new CancelAuthorizationRequest(order, id), PaymentStatus.Authorized);
        var capture = await host.Post<CapturePaymentResponse>("captures", new CapturePaymentRequest(order, id));
        return (new IssueInvoiceRequest(order, capture.CaptureId), PaymentStatus.Captured);
    }

    private static async Task<PaymentSnapshot> WaitForEffect(PaymentHost host, Guid order, PaymentStatus expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            using var response = await host.Client.GetAsync($"/api/v1/payments/{order}", deadline.Token);
            if (response.IsSuccessStatusCode)
            {
                var snapshot = WireJson.Read<PaymentSnapshot>(await response.Content.ReadAsStringAsync(deadline.Token));
                if (snapshot.Status == expected) return snapshot;
            }
            await Task.Delay(20, deadline.Token);
        }
    }
}

internal sealed class PaymentData : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "playground-payments-" + Guid.NewGuid().ToString("N"));
    public LabSettings Settings => new("payment-tests", Root, "127.0.0.1", 1, "unused", "unused");
    public long Count(string sql)
    {
        using var connection = LocalStore.Open(Settings.Database("Payments.Api"));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
    public void Dispose()
    {
        using var connection = LocalStore.Open(Settings.Database("Payments.Api"));
        SqliteConnection.ClearPool(connection);
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

internal sealed class PaymentHost(WebApplication app, HttpClient client) : IAsyncDisposable
{
    public HttpClient Client { get; } = client;
    public static async Task<PaymentHost> Start(PaymentData data, PaymentSimulation? simulation = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(data.Settings);
        builder.Services.AddSingleton(simulation ?? new("happy-path", 0, 0, null));
        builder.Services.AddSingleton<PaymentStore>();
        var app = builder.Build();
        app.Services.GetRequiredService<PaymentStore>().Initialize();
        app.MapPayments();
        await app.StartAsync();
        return new(app, new() { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(15) });
    }
    public async Task<T> Post<T>(string operation, object request) where T : IContract
    {
        using var response = await Client.PostAsJsonAsync("/api/v1/" + operation, request, WireJson.Options);
        var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {json}");
        return WireJson.Read<T>(json);
    }
    public async Task Error(string operation, object request, HttpStatusCode status, string code)
    {
        using var response = await Client.PostAsJsonAsync("/api/v1/" + operation, request, WireJson.Options);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, WireJson.Read<ApiError>(await response.Content.ReadAsStringAsync()).Code);
    }
    public async Task<PaymentSnapshot> Snapshot(Guid order)
    {
        using var response = await Client.GetAsync($"/api/v1/payments/{order}");
        response.EnsureSuccessStatusCode();
        return WireJson.Read<PaymentSnapshot>(await response.Content.ReadAsStringAsync());
    }
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
