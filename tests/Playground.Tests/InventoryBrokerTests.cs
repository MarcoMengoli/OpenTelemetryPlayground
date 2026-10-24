using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Inventory.Service;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Playground.Contracts;
using Playground.Infrastructure;
using RabbitMQ.Client;
using Xunit;

namespace Playground.Tests;

[Trait("Category", "Broker")]
public sealed class InventoryBrokerTests
{
    [BrokerFact]
    public async Task Real_consumer_reserves_once_for_duplicate_and_concurrent_orders()
    {
        await using var lab = await InventoryBrokerLab.Create();
        await using var host = await lab.Start();
        var original = InventoryData.Message();
        var messages = Enumerable.Range(0, 6).Select(index => index % 2 == 0 ? original : original with { MessageId = Guid.NewGuid() })
            .Concat(Enumerable.Range(1, 10).Select(quantity => InventoryData.Message() with { Items = [new("BOOK", quantity, 1m)] })).ToArray();
        await Task.WhenAll(messages.Select(lab.Publish));
        await lab.Idle(16);
        Assert.Equal(57, lab.Scalar("SELECT reserved FROM inventory_stock WHERE sku = 'BOOK'"));
        Assert.Equal(3, lab.Scalar("SELECT reserved FROM inventory_stock WHERE sku = 'PEN'"));
        Assert.Equal(12, lab.Scalar("SELECT COUNT(*) FROM inventory_reservations"));
        Assert.Equal(14, lab.Scalar("SELECT COUNT(*) FROM inventory_inbox WHERE processed = 1"));
        Assert.Equal(11, lab.Scalar("SELECT COUNT(*) FROM inventory_work WHERE completed = 1"));
    }

    [BrokerFact]
    public async Task Invalid_and_conflicting_messages_dead_letter_without_blocking_the_next_valid_order()
    {
        await using var lab = await InventoryBrokerLab.Create();
        await using var host = await lab.Start();
        var message = InventoryData.Message();
        await lab.Publish(message);
        await lab.Wait("SELECT completed FROM inventory_work", 1);
        await using var connection = await Broker.Factory(lab.Settings).CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        var invalid = message with { MessageId = Guid.NewGuid(), OrderId = Guid.NewGuid() };
        foreach (var body in new[] { "{}", WireJson.Write(invalid).Replace("\"quantity\":2", "\"quantity\":0"), WireJson.Write(invalid).Replace("\"schemaVersion\":1", "\"schemaVersion\":2") })
            await channel.BasicPublishAsync(lab.Settings.Exchange, "inventory.reserve.v1", true, InventoryData.Properties(invalid), Encoding.UTF8.GetBytes(body));
        var wrongProperties = InventoryData.Properties(invalid);
        wrongProperties.MessageId = Guid.NewGuid().ToString();
        await channel.BasicPublishAsync(lab.Settings.Exchange, "inventory.reserve.v1", true, wrongProperties, Encoding.UTF8.GetBytes(WireJson.Write(invalid)));
        await lab.Publish(message with { MessageId = Guid.NewGuid(), Items = [new("BOOK", 8, 1m)] });
        await lab.Publish(message with { OrderId = Guid.NewGuid() });
        for (var index = 0; index < 6; index++)
            _ = await OrderBrokerLab.Receive(channel, lab.Queue + ".errors");
        await lab.Publish(InventoryData.Message());
        await lab.Idle(2);
        Assert.Equal(2, lab.Scalar("SELECT COUNT(*) FROM inventory_work WHERE completed = 1"));
        Assert.Equal(2, lab.Scalar("SELECT COUNT(*) FROM inventory_inbox"));
        Assert.Equal(4, lab.Scalar("SELECT reserved FROM inventory_stock WHERE sku = 'BOOK'"));
        Assert.Null(await channel.BasicGetAsync(lab.Queue + ".errors", autoAck: true));
    }

    [BrokerFact]
    public async Task Stop_before_commit_preserves_the_attempt_budget_and_restart_resumes_the_same_reservation()
    {
        await using var lab = await InventoryBrokerLab.Create();
        var message = InventoryData.Message();
        var simulation = new InventorySimulation("temporary-inventory-failure", 0, 2, message.OrderId);
        await using (var first = await lab.Start(simulation))
        {
            await lab.Publish(message);
            await lab.Wait("SELECT attempts FROM inventory_work WHERE last_error = 'reservation_temporarily_unavailable'", 1);
            await first.App.StopAsync();
        }
        Assert.Equal(0, lab.Scalar("SELECT completed FROM inventory_work"));
        Assert.Equal(1, lab.Scalar("SELECT attempts FROM inventory_work"));
        Assert.Equal(0, lab.Scalar("SELECT COUNT(*) FROM inventory_reservations"));
        await using var restarted = await lab.Start(simulation);
        await lab.Idle(1);
        Assert.Equal(3, lab.Scalar("SELECT attempts FROM inventory_work"));
        Assert.Equal(2, lab.Scalar("SELECT reserved FROM inventory_stock WHERE sku = 'BOOK'"));
        Assert.Equal(1, lab.Scalar("SELECT COUNT(*) FROM inventory_inbox WHERE processed = 1"));
    }

    [BrokerFact]
    public async Task Stop_after_commit_before_ack_redelivers_but_never_reserves_twice()
    {
        await using var lab = await InventoryBrokerLab.Create();
        var message = InventoryData.Message();
        await using (var first = await lab.Start(new("happy-path", 60000, 0, message.OrderId)))
        {
            await lab.Publish(message);
            await lab.Wait("SELECT completed FROM inventory_work", 1);
            await lab.Unacknowledged();
            await first.App.StopAsync();
        }
        await using (var connection = await Broker.Factory(lab.Settings).CreateConnectionAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            BasicGetResult? delivery;
            while ((delivery = await channel.BasicGetAsync(lab.Queue, autoAck: false, deadline.Token)) is null) await Task.Delay(50, deadline.Token);
            Assert.True(delivery.Redelivered);
            Assert.Equal(message.MessageId.ToString(), delivery.BasicProperties.MessageId);
            // Closing this observer channel leaves the original delivery unacked.
            await channel.CloseAsync();
        }
        await using var restarted = await lab.Start();
        await lab.Idle(1);
        Assert.Equal(2, lab.Scalar("SELECT reserved FROM inventory_stock WHERE sku = 'BOOK'"));
        Assert.Equal(3, lab.Scalar("SELECT reserved FROM inventory_stock WHERE sku = 'PEN'"));
        Assert.Equal(1, lab.Scalar("SELECT attempts FROM inventory_work"));
        Assert.Equal(1, lab.Scalar("SELECT COUNT(*) FROM inventory_inbox WHERE processed = 1"));
    }

    [BrokerFact]
    public async Task Exhaustion_dead_letters_once_and_requires_an_explicit_operator_cycle()
    {
        await using var lab = await InventoryBrokerLab.Create();
        var message = InventoryData.Message();
        await using var connection = await Broker.Factory(lab.Settings).CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await using (var first = await lab.Start(new("temporary-inventory-failure", 0, 3, message.OrderId)))
        {
            await lab.Publish(message);
            var failed = await OrderBrokerLab.Receive(channel, lab.Queue + ".errors");
            Assert.Equal(message.MessageId.ToString(), failed.BasicProperties.MessageId);
        }
        Assert.Equal(3, lab.Scalar("SELECT attempts FROM inventory_work"));
        Assert.Equal("reservation_retry_exhausted", lab.Query("SELECT last_error FROM inventory_work"));
        await using (var restarted = await lab.Start())
        {
            await lab.Publish(message);
            _ = await OrderBrokerLab.Receive(channel, lab.Queue + ".errors");
            Assert.Equal(3, lab.Scalar("SELECT attempts FROM inventory_work"));
            Assert.Equal(0, lab.Scalar("SELECT COUNT(*) FROM inventory_reservations"));
        }
        lab.Query("UPDATE inventory_work SET attempts = 0, last_error = NULL WHERE completed = 0");
        await using var recovered = await lab.Start();
        await lab.Wait("SELECT completed FROM inventory_work", 1);
        await lab.Publish(message);
        await lab.Idle(1);
        Assert.Equal(2, lab.Scalar("SELECT reserved FROM inventory_stock WHERE sku = 'BOOK'"));
        Assert.Equal(1, lab.Scalar("SELECT COUNT(*) FROM inventory_inbox"));
        Assert.Null(await channel.BasicGetAsync(lab.Queue + ".errors", autoAck: true));
    }
}

internal sealed class InventoryBrokerLab(LabSettings settings, HttpClient management) : IAsyncDisposable
{
    public LabSettings Settings { get; } = settings;
    public string Queue => Settings.Name + ".inventory.reserve.v1";
    private bool created;
    public static async Task<InventoryBrokerLab> Create()
    {
        var source = LabSettings.Read(new ConfigurationBuilder().AddEnvironmentVariables().Build());
        var name = "us05-" + Guid.NewGuid().ToString("N")[..20];
        var settings = source with { Name = name, Root = Path.Combine(Path.GetTempPath(), "playground-" + name) };
        var management = new HttpClient { BaseAddress = new Uri($"http://{source.RabbitHost}:{source.RabbitPort + 1}"), Timeout = TimeSpan.FromSeconds(10) };
        management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(source.RabbitUser + ":" + source.RabbitPassword)));
        var lab = new InventoryBrokerLab(settings, management);
        try
        {
            using var result = await management.PutAsJsonAsync("/api/vhosts/" + name, new { });
            result.EnsureSuccessStatusCode();
            lab.created = true;
            using var permission = await management.PutAsJsonAsync("/api/permissions/" + name + "/" + Uri.EscapeDataString(source.RabbitUser), new { configure = ".*", write = ".*", read = ".*" });
            permission.EnsureSuccessStatusCode();
            await using var connection = await Broker.Factory(settings).CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            await Broker.DeclareAsync(channel, settings, CancellationToken.None);
            return lab;
        }
        catch { await lab.DisposeAsync(); throw; }
    }

    public Task<OrderTestHost> Start(InventorySimulation? simulation = null)
    {
        simulation ??= new("happy-path", 0, 0, null);
        var args = new List<string>
        {
            "--urls", "http://127.0.0.1:0", "--Lab:Name", Settings.Name, "--Lab:Root", Settings.Root,
            "--Lab:RabbitHost", Settings.RabbitHost, "--Lab:RabbitPort", Settings.RabbitPort.ToString(),
            "--Lab:RabbitUser", Settings.RabbitUser, "--Lab:RabbitPassword", Settings.RabbitPassword,
            "--Simulation:Profile", simulation.Profile, "--Simulation:DelayMs", simulation.DelayMs.ToString(),
            "--Simulation:FailuresBeforeSuccess", simulation.FailuresBeforeSuccess.ToString()
        };
        if (simulation.OrderId is Guid id) args.AddRange(["--Simulation:OrderId", id.ToString()]);
        return OrderTestHost.Start(InventoryApplication.Build(args.ToArray()));
    }
    public async Task Publish(ReserveInventory message)
    {
        await using var publisher = new ConfirmedPublisher(Settings);
        await publisher.PublishAsync(new(message.MessageId, message.OrderId, "ReserveInventory.v1", "inventory.reserve.v1", WireJson.Write(message)), CancellationToken.None);
    }
    public object? Query(string sql) => OrderTestData.Query(Settings, "Inventory.Service", sql);
    public long Scalar(string sql) => Convert.ToInt64(Query(sql));
    public async Task Wait(string sql, long expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (Scalar(sql) != expected) await Task.Delay(25, deadline.Token);
    }
    // Management statistics arrive asynchronously after queue creation. Missing
    // measurements mean "not observed yet", never zero messages or a passed check.
    public Task Idle(long acknowledgements) => QueueState(root => root.TryGetProperty("messages", out var messages) && messages.GetInt64() == 0 &&
        root.TryGetProperty("consumers", out var consumers) && consumers.GetInt64() == 1 &&
        root.TryGetProperty("message_stats", out var stats) && stats.TryGetProperty("ack", out var ack) && ack.GetInt64() >= acknowledgements);
    public Task Unacknowledged() => QueueState(root => root.TryGetProperty("messages_unacknowledged", out var messages) && messages.GetInt64() == 1);
    private async Task QueueState(Func<JsonElement, bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            using var response = await management.GetAsync("/api/queues/" + Settings.Name + "/" + Queue, deadline.Token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            if (predicate(json.RootElement)) return;
            await Task.Delay(100, deadline.Token);
        }
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (created)
            {
                using var deleted = await management.DeleteAsync("/api/vhosts/" + Settings.Name);
                if (deleted.StatusCode != HttpStatusCode.NotFound) deleted.EnsureSuccessStatusCode();
            }
        }
        finally
        {
            management.Dispose();
            using var connection = LocalStore.Open(Settings.Database("Inventory.Service"));
            SqliteConnection.ClearPool(connection);
            if (Directory.Exists(Settings.Root)) Directory.Delete(Settings.Root, recursive: true);
        }
    }
}
