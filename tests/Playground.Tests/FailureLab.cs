using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Playground.Contracts;
using Playground.Infrastructure;
using RabbitMQ.Client;
using Xunit;
using Xunit.Abstractions;

namespace Playground.Tests;

[CollectionDefinition("Failure scenarios", DisableParallelization = true)]
public sealed class FailureScenarioCollection;

internal sealed class FailureLab : IAsyncDisposable
{
    public static readonly string[] Services = ["Payments.Api", "Couriers.Api", "Fulfillment.Service", "Inventory.Service",
        "Orders.Service", "Checkout.Api", "Warehouse.Simulator", "WarehouseResponse.Service"];
    private readonly string name = "us11-" + Guid.NewGuid().ToString("N")[..24];
    private readonly ITestOutputHelper output;
    private readonly Dictionary<string, TcpListener> reservations = [];
    private readonly Dictionary<string, Uri> urls = [];
    private readonly Dictionary<string, ChildHost> processes = [];
    private readonly Dictionary<string, string[]> configuration = [];
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private bool containerCreated;
    public LabSettings Settings { get; private set; } = null!;
    public Uri? PaymentsProxy { get; set; }
    public Uri Url(string service) => urls[service];
    private FailureLab(ITestOutputHelper output) { this.output = output; }
    public static async Task<FailureLab> Create(ITestOutputHelper output)
    {
        var lab = new FailureLab(output);
        lab.Settings = new(lab.name, Path.Combine(Path.GetTempPath(), "playground-" + lab.name), "127.0.0.1", 1,
            "playground", "local-playground-only");
        try
        {
            foreach (var service in Services)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                lab.reservations[service] = listener;
                lab.urls[service] = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port);
            }
            Directory.CreateDirectory(lab.Settings.Root);
            await Docker("create", "--name", lab.name, "-p", "127.0.0.1::5672",
                "-e", "RABBITMQ_DEFAULT_USER=playground", "-e", "RABBITMQ_DEFAULT_PASS=local-playground-only",
                "-e", "RABBITMQ_DEFAULT_VHOST=" + lab.name, "rabbitmq:4.1.4-management");
            lab.containerCreated = true;
            await lab.StartBroker();
            return lab;
        }
        catch { await lab.DisposeAsync(); throw; }
    }
    public void Configure(string service, params string[] args) => configuration[service] = args;
    public async Task StartAll()
    {
        foreach (var service in Services) if (!processes.ContainsKey(service)) await Start(service);
    }
    public async Task Start(string service)
    {
        if (processes.ContainsKey(service)) throw new InvalidOperationException("Stop the owned process before restarting it.");
        if (reservations.Remove(service, out var listener)) listener.Stop();
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "OpenTelemetryPlayground.slnx"))) repo = repo.Parent;
        if (repo is null) throw new InvalidOperationException("Repository root not found.");
        var dll = Path.Combine(repo.FullName, "src", service, "bin", "Release", "net10.0", service + ".dll");
        if (!File.Exists(dll)) throw new InvalidOperationException("Build the complete solution in Release first: " + dll);
        var info = new ProcessStartInfo("dotnet") { WorkingDirectory = repo.FullName, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        // Do not inherit another lab's simulation or endpoint selection.
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("Simulation__", StringComparison.OrdinalIgnoreCase) ||
            k.StartsWith("Lab__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Services__", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        string[] args = [dll, "--urls", Url(service).ToString(), "--Lab:Name", Settings.Name, "--Lab:Root", Settings.Root,
            "--Lab:RabbitHost", Settings.RabbitHost, "--Lab:RabbitPort", Settings.RabbitPort.ToString(),
            "--Lab:RabbitUser", Settings.RabbitUser, "--Lab:RabbitPassword", Settings.RabbitPassword,
            "--Services:PaymentsBaseUrl", (PaymentsProxy ?? Url("Payments.Api")).ToString(),
            "--Services:OrdersBaseUrl", Url("Orders.Service").ToString(),
            "--Services:FulfillmentBaseUrl", Url("Fulfillment.Service").ToString(),
            "--Services:CouriersUrl", Url("Couriers.Api").ToString(),
            "--Simulation:Profile", "happy-path", "--Simulation:DelayMs", "0", "--Simulation:FailuresBeforeSuccess", "0"];
        foreach (var arg in args.Concat(configuration.GetValueOrDefault(service) ?? [])) info.ArgumentList.Add(arg);
        var child = new ChildHost(Process.Start(info) ?? throw new InvalidOperationException("Process failed to start."));
        processes[service] = child;
        await Until(async () =>
        {
            if (child.Process.HasExited) throw new InvalidOperationException(service + " exited: " + child.Tail());
            try { using var ready = await http.GetAsync(new Uri(Url(service), "/health/ready")); return ready.IsSuccessStatusCode; }
            catch (HttpRequestException) { return false; }
        }, service + " readiness", 30);
    }
    public async Task Stop(string service, bool kill = false)
    {
        if (!processes.Remove(service, out var child)) return;
        try
        {
            if (!child.Process.HasExited)
            {
                if (kill) child.Process.Kill(entireProcessTree: true);
                else
                {
                    var path = Path.Combine(Settings.Root, "run", service + ".stop");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, "US11 fixture owns this process");
                }
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await child.Process.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException)
                {
                    child.Process.Kill(entireProcessTree: true);
                    await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    throw new TimeoutException(service + " did not stop within 15s.");
                }
            }
        }
        finally
        {
            await child.Drain();
            output.WriteLine(service + " exit=" + child.Process.ExitCode + "\n" + child.Tail());
            child.Process.Dispose();
        }
    }
    public async Task StopAll()
    {
        foreach (var service in Services.Reverse()) await Stop(service);
    }
    public Task StopBroker() => Docker("stop", "--time", "10", name);
    public async Task StartBroker()
    {
        await Docker("start", name);
        var port = await Docker("port", name, "5672/tcp");
        Settings = Settings with { RabbitPort = int.Parse(port.Trim().Split(':').Last()) };
        await Until(async () =>
        {
            try
            {
                var factory = Broker.Factory(Settings);
                factory.AutomaticRecoveryEnabled = false;
                await using var connection = await factory.CreateConnectionAsync();
                await using var channel = await connection.CreateChannelAsync();
                await Broker.DeclareAsync(channel, Settings, default);
                return true;
            }
            catch (RabbitMQ.Client.Exceptions.BrokerUnreachableException) { return false; }
        }, "private broker readiness", 90);
    }
    public async Task<CheckoutRequest> Checkout()
    {
        var order = new CheckoutRequest(Guid.NewGuid(), [new("BOOK", 2, 12.50m)], new("Ada", "Via Roma 1", "Bologna", "40100", "IT"));
        using var response = await http.PostAsync(new Uri(Url("Checkout.Api"), "/api/v1/orders"),
            new StringContent(WireJson.Write(order), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return order;
    }
    public async Task<OrderSnapshot> Snapshot(Guid order)
    {
        using var response = await http.GetAsync(new Uri(Url("Checkout.Api"), "/api/v1/orders/" + order));
        response.EnsureSuccessStatusCode();
        return WireJson.Read<OrderSnapshot>(await response.Content.ReadAsStringAsync());
    }
    public async Task<OrderSnapshot> Completed(Guid order)
    {
        OrderSnapshot? snapshot = null;
        await Until(async () => { snapshot = await Snapshot(order); return snapshot.Status == OrderStatus.Completed; }, "order completed", 90);
        await WaitCount("Inventory.Service", "SELECT COUNT(*) FROM inventory_work WHERE completed=1", 1);
        return snapshot!;
    }
    public object? Query(string service, string sql, params (string, object)[] parameters)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Settings.Database(service), Pooling = false, DefaultTimeout = 5 }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value);
        return command.ExecuteScalar();
    }
    public long Count(string service, string sql) => Convert.ToInt64(Query(service, sql));
    public Task WaitCount(string service, string sql, long expected, int seconds = 60) =>
        Until(() => Task.FromResult(Count(service, sql) == expected), service + ": " + sql, seconds);
    public string Text(string service, string sql) => (string)Query(service, sql)!;
    public string[] Files(string relative)
    {
        var path = Path.Combine(Settings.Warehouse, relative);
        return Directory.Exists(path) ? Directory.GetFiles(path, "*.json") : [];
    }
    public void Put(string folder, string filename, string json) => WarehouseFiles.Publish(Path.Combine(Settings.Warehouse, folder), filename, json);
    public async Task Publish(OutboxMessage message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var publisher = new ConfirmedPublisher(Settings);
        await publisher.PublishAsync(message, timeout.Token);
    }
    public OutboxMessage Message(string service, string type)
    {
        var json = Text(service, "SELECT body FROM outbox WHERE type='" + type + ".v1'");
        var id = Guid.Parse(Text(service, "SELECT message_id FROM outbox WHERE type='" + type + ".v1'"));
        var order = Guid.Parse(Text(service, "SELECT order_id FROM outbox WHERE type='" + type + ".v1'"));
        var route = Text(service, "SELECT routing_key FROM outbox WHERE type='" + type + ".v1'");
        return new(id, order, type + ".v1", route, json);
    }
    public async Task<string> ErrorMessage(string suffix)
    {
        await using var connection = await Broker.Factory(Settings).CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        string? result = null;
        await Until(async () =>
        {
            var received = await channel.BasicGetAsync(Settings.Name + "." + suffix + ".errors", false);
            if (received is null) return false;
            result = received.BasicProperties.MessageId;
            await channel.BasicNackAsync(received.DeliveryTag, false, true);
            return true;
        }, "durable error queue", 45);
        return result!;
    }
    public void AssertSingleEffects(OrderSnapshot snapshot)
    {
        Assert.Equal(OrderStatus.Completed, snapshot.Status);
        Assert.Null(snapshot.LastError);
        Assert.NotNull(snapshot.AuthorizationId);
        Assert.NotNull(snapshot.ShipmentId);
        Assert.NotNull(snapshot.CaptureId);
        Assert.NotNull(snapshot.InvoiceId);
        Assert.Equal(1, Count("Payments.Api", "SELECT COUNT(*) FROM payments WHERE status='Invoiced'"));
        Assert.Equal(snapshot.CaptureId!.Value.ToString(), Text("Payments.Api", "SELECT capture_id FROM payments"));
        Assert.Equal(snapshot.InvoiceId!.Value.ToString(), Text("Payments.Api", "SELECT invoice_id FROM payments"));
        Assert.Equal(2, Count("Inventory.Service", "SELECT reserved FROM inventory_stock WHERE sku='BOOK'"));
        Assert.Equal(1, Count("Inventory.Service", "SELECT COUNT(*) FROM inventory_reservations"));
        Assert.Equal(1, Count("Fulfillment.Service", "SELECT COUNT(*) FROM fulfillment_work"));
        Assert.Equal(1, Count("Warehouse.Simulator", "SELECT COUNT(*) FROM warehouse_shipments"));
        Assert.Equal(1, Count("WarehouseResponse.Service", "SELECT COUNT(*) FROM outbox"));
        Assert.Equal(1, Count("Orders.Service", "SELECT COUNT(*) FROM shipment_work"));
    }
    public static async Task Until(Func<Task<bool>> condition, string description, int seconds = 60)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Deadline " + seconds + "s exceeded: " + description);
    }
    private static async Task<string> Docker(params string[] arguments)
    {
        var info = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Docker unavailable.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var result = await stdout;
        if (process.ExitCode != 0) throw new InvalidOperationException("Docker " + arguments[0] + ": " + await stderr);
        await stderr;
        return result;
    }
    public async ValueTask DisposeAsync()
    {
        var errors = new List<Exception>();
        foreach (var service in Services.Reverse())
            try { await Stop(service); } catch (Exception e) { errors.Add(e); }
        foreach (var listener in reservations.Values) listener.Stop();
        http.Dispose();
        try { if (containerCreated) await Docker("rm", "--force", "--volumes", name); }
        catch (Exception e) { errors.Add(e); }
        // Only this fixture's generated UUID root, after all owned processes have exited.
        if (errors.Count == 0 && processes.Count == 0 && Directory.Exists(Settings.Root)) Directory.Delete(Settings.Root, recursive: true);
        if (errors.Count > 0) throw new AggregateException("US11 cleanup failed", errors);
    }
    private sealed class ChildHost
    {
        public Process Process { get; }
        private readonly ConcurrentQueue<string> lines = new();
        private readonly Task stdout;
        private readonly Task stderr;
        public ChildHost(Process process) { Process = process; stdout = Read(process.StandardOutput); stderr = Read(process.StandardError); }
        private async Task Read(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line) { lines.Enqueue(line); while (lines.Count > 35) lines.TryDequeue(out _); }
        }
        public string Tail() => string.Join("\n", lines);
        public Task Drain() => Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
    }
}

// A transparent HTTP adapter gates an actual Payments request/response only at
// the named crash boundary. No fabricated mapping, payment or business result.
internal sealed class PaymentBoundary(WebApplication app, HttpClient client) : IAsyncDisposable
{
    public Uri Address => new(app.Urls.Single());
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static async Task<PaymentBoundary> Start(Uri payments, bool afterRemoteCapture)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var boundary = new PaymentBoundary(app, new HttpClient { BaseAddress = payments, Timeout = TimeSpan.FromSeconds(10) });
        app.MapPost("/api/v1/{operation}", async (string operation, HttpContext context) =>
        {
            if (!afterRemoteCapture && operation == "invoices")
            {
                boundary.Reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            using var body = new StreamContent(context.Request.Body);
            body.Headers.ContentType = new("application/json");
            using var response = await boundary.client.PostAsync("/api/v1/" + operation, body, context.RequestAborted);
            var json = await response.Content.ReadAsStringAsync(context.RequestAborted);
            if (afterRemoteCapture && operation == "captures")
            {
                boundary.Reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            return Results.Content(json, "application/json", statusCode: (int)response.StatusCode);
        });
        await app.StartAsync();
        return boundary;
    }
    private readonly HttpClient client = client;
    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); client.Dispose(); }
}
