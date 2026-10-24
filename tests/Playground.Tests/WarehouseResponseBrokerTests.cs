using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Playground.Contracts;
using Playground.Infrastructure;
using Playground.WarehouseResponses;
using RabbitMQ.Client;
using Xunit;

namespace Playground.Tests;

[Trait("Category", "Broker")]
public sealed class WarehouseResponseBrokerTests
{
    [BrokerFact]
    public async Task Real_broker_confirm_crash_redelivery_and_stopped_broker_recovery_preserve_identity()
    {
        await using var broker = await ResponseBroker.Create();
        using var data = new ResponseData(broker.Settings);
        await using var lookup = await ResponseLookupHost.Start(data.Settings);
        var (response, order) = data.KnownResponse();
        var source = data.Put("first.json", WireJson.Write(response));
        data.Reader().Scan(data.Now);
        await lookup.Lookup(data.Store()).ResumePendingAsync(data.Now, default);

        // Consumer restart after durable registration, before publish.
        var store = data.Store();
        var outbox = new DurableOutbox(store.Database);
        var original = outbox.Next()!;
        Assert.Equal(order, WireJson.Read<ShipmentConfirmation>(original.Body).OrderId);
        outbox.StartAttempt(original.MessageId);
        await using (var firstPublisher = new ConfirmedPublisher(data.Settings))
            await firstPublisher.PublishAsync(original, default);
        // Confirm reached, then process lost before MarkSent and file archive.
        Assert.True(File.Exists(source));
        Assert.Equal(0L, data.Query("SELECT sent FROM outbox"));
        outbox = new DurableOutbox(data.Store().Database);
        outbox.StartCycle();
        var pump = new OutboxPump(data.Settings, outbox, NullLogger<OutboxPump>.Instance);
        await using (var restartedPublisher = new ConfirmedPublisher(data.Settings))
            Assert.True(await pump.SendNextAsync(restartedPublisher, default));
        Assert.Equal(1L, data.Query("SELECT sent FROM outbox"));

        await using (var connection = await Broker.Factory(data.Settings).CreateConnectionAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            var first = await Receive(channel, data.Settings);
            var redelivery = await Receive(channel, data.Settings);
            foreach (var message in new[] { first, redelivery })
            {
                Assert.Equal(original.Body, Encoding.UTF8.GetString(message.Body.Span));
                Assert.Equal(original.MessageId.ToString(), message.BasicProperties.MessageId);
                Assert.Equal("ShipmentConfirmation.v1", message.BasicProperties.Type);
                Assert.Equal("application/json", message.BasicProperties.ContentType);
                Assert.True(message.BasicProperties.Persistent);
                Assert.True(message.BasicProperties.Headers is null || message.BasicProperties.Headers.Count == 0);
            }
            data.Put("renamed.json", WireJson.Write(response));
            data.Reader().Scan(data.Now);
            Assert.Equal(2, data.Archived("processed").Length);
            Assert.Equal(1L, data.Query("SELECT COUNT(*) FROM outbox"));
            Assert.Null(await channel.BasicGetAsync(data.Settings.Name + ".orders.shipment-confirmation.v1", true));
        }

        var (secondResponse, secondOrder) = data.KnownResponse();
        data.Put("second.json", WireJson.Write(secondResponse));
        data.Reader().Scan(data.Now);
        await lookup.Lookup(data.Store()).ResumePendingAsync(data.Now, default);
        var pending = outbox.Next()!;
        Assert.Equal(secondOrder, pending.OrderId);
        // Only the UUID-owned container is stopped. No shared lab or A resources.
        await broker.Stop();
        await using (var unavailable = new ConfirmedPublisher(data.Settings))
            for (var i = 0; i < 3; i++) Assert.True(await pump.SendNextAsync(unavailable, default));
        Assert.Equal(3L, data.Query("SELECT attempts FROM outbox WHERE sent=0"));
        Assert.Equal("publish_retry_exhausted", outbox.Status(secondOrder).LastError);
        data.Reader().Scan(data.Now);
        Assert.Equal(3, data.Archived("processed").Length); // Durable outbox survives file archive.

        outbox = new DurableOutbox(data.Store().Database);
        outbox.StartCycle();
        Assert.Null(outbox.Next()); // Restart does not silently renew exhausted budgets.
        await broker.Start();
        // Explicit operator recovery after correcting the cause, preserving ID and body.
        data.Query("UPDATE outbox SET attempts=0,last_error=NULL WHERE sent=0");
        Assert.Equal(pending, outbox.Next());
        await using (var recovered = new ConfirmedPublisher(data.Settings))
            Assert.True(await new OutboxPump(data.Settings, outbox, NullLogger<OutboxPump>.Instance).SendNextAsync(recovered, default));
        await using (var connection = await Broker.Factory(data.Settings).CreateConnectionAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            var message = await Receive(channel, data.Settings);
            Assert.Equal(pending.Body, Encoding.UTF8.GetString(message.Body.Span));
            Assert.Equal(pending.MessageId.ToString(), message.BasicProperties.MessageId);
            Assert.Null(await channel.BasicGetAsync(data.Settings.Name + ".orders.shipment-confirmation.v1", true));
        }
        Assert.Equal(2L, data.Query("SELECT COUNT(*) FROM outbox WHERE sent=1"));
        Assert.Equal(2L, data.Query("SELECT COUNT(*) FROM responses WHERE state='Ready'"));
    }
    private static async Task<BasicGetResult> Receive(IChannel channel, LabSettings settings)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var message = await channel.BasicGetAsync(settings.Name + ".orders.shipment-confirmation.v1", true, deadline.Token);
            if (message is not null) return message;
            await Task.Delay(50, deadline.Token);
        }
    }
}

// Destructive operations are limited to the name generated by this fixture.
// A private container (not just a private vhost) permits a genuine broker-stop test.
internal sealed class ResponseBroker(string name) : IAsyncDisposable
{
    public LabSettings Settings { get; private set; } = null!;
    private bool created;
    public static async Task<ResponseBroker> Create()
    {
        var name = "us09-" + Guid.NewGuid().ToString("N")[..24];
        var fixture = new ResponseBroker(name);
        try
        {
            await Docker("create", "--name", name, "-p", "127.0.0.1::5672",
                "-e", "RABBITMQ_DEFAULT_USER=playground", "-e", "RABBITMQ_DEFAULT_PASS=local-playground-only",
                "-e", "RABBITMQ_DEFAULT_VHOST=" + name, "rabbitmq:4.1.4-management");
            fixture.created = true;
            await Docker("start", name);
            var port = await Docker("port", name, "5672/tcp");
            fixture.Settings = new(name, Path.Combine(Path.GetTempPath(), "playground-" + name), "127.0.0.1",
                int.Parse(port.Trim().Split(':').Last()), "playground", "local-playground-only");
            await fixture.Ready();
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }
    public Task Stop() => Docker("stop", "--time", "10", name);
    public async Task Start() { await Docker("start", name); await Ready(); }
    private async Task Ready()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            try
            {
                var factory = Broker.Factory(Settings);
                factory.AutomaticRecoveryEnabled = false;
                await using var connection = await factory.CreateConnectionAsync(deadline.Token);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: deadline.Token);
                await Broker.DeclareAsync(channel, Settings, deadline.Token);
                return;
            }
            catch (Exception) when (!deadline.IsCancellationRequested) { await Task.Delay(500, deadline.Token); }
        }
    }
    private static async Task<string> Docker(params string[] arguments)
    {
        var info = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Docker did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var stdout = await output;
        var stderr = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException("Docker " + arguments[0] + " failed: " + stderr);
        return stdout;
    }
    public async ValueTask DisposeAsync()
    {
        if (created) await Docker("rm", "--force", "--volumes", name);
    }
}
