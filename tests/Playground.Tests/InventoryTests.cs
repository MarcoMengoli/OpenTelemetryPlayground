using System.Text;
using System.Text.Json;
using Inventory.Service;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Playground.Contracts;
using Playground.Infrastructure;
using RabbitMQ.Client;
using Xunit;

namespace Playground.Tests;

public sealed class InventoryTests
{
    [Fact]
    public async Task Concurrent_transport_and_business_duplicates_reserve_each_line_once_after_reopen()
    {
        using var data = new InventoryData();
        var message = InventoryData.Message();
        var processor = data.Processor();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(async () =>
        {
            var duplicate = index % 2 == 0 ? message : message with { MessageId = Guid.NewGuid(), Items = message.Items.Reverse().ToArray() };
            Assert.True(await processor.ProcessAsync(duplicate, CancellationToken.None));
        })));
        Assert.Equal(2, data.Scalar("SELECT COUNT(*) FROM inventory_reservations"));
        Assert.Equal(2, data.Reserved("BOOK"));
        Assert.Equal(3, data.Reserved("PEN"));
        Assert.Equal(7, data.Scalar("SELECT COUNT(*) FROM inventory_inbox WHERE processed = 1"));
        var reopened = data.Reopen();
        Assert.True(await data.Processor(store: reopened).ProcessAsync(message, CancellationToken.None));
        Assert.Equal(2, data.Reserved("BOOK"));
        Assert.Equal(3, data.Reserved("PEN"));
    }

    [Fact]
    public async Task Concurrent_database_commits_for_the_same_reservation_keep_one_effect()
    {
        using var data = new InventoryData();
        var message = InventoryData.Message();
        Assert.False(data.Store.Acquire(message));
        Assert.Equal(1, data.Store.StartAttempt(message.OrderId));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => data.Store.Complete(message.OrderId))));
        Assert.Equal(2, data.Reserved("BOOK"));
        Assert.Equal(3, data.Reserved("PEN"));
        Assert.Equal(2, data.Scalar("SELECT COUNT(*) FROM inventory_reservations"));
        Assert.Equal(1, data.Scalar("SELECT processed FROM inventory_inbox"));
    }

    [Fact]
    public async Task Different_orders_for_the_same_SKU_do_not_lose_concurrent_updates()
    {
        using var data = new InventoryData();
        await Task.WhenAll(Enumerable.Range(1, 12).Select(quantity => Task.Run(async () =>
        {
            var message = InventoryData.Message() with { Items = [new("BOOK", quantity, 1m)] };
            Assert.True(await data.Processor().ProcessAsync(message, CancellationToken.None));
        })));
        Assert.Equal(78, data.Reserved("BOOK"));
        Assert.Equal(12, data.Scalar("SELECT COUNT(*) FROM inventory_reservations"));
        Assert.Equal(12, data.Scalar("SELECT COUNT(*) FROM inventory_inbox WHERE processed = 1"));
    }

    [Fact]
    public async Task Incompatible_message_or_business_key_is_rejected_without_new_effects()
    {
        using var data = new InventoryData();
        var message = InventoryData.Message();
        Assert.True(await data.Processor().ProcessAsync(message, CancellationToken.None));
        Assert.Throws<ReservationConflictException>(() => data.Store.Acquire(message with { OrderId = Guid.NewGuid() }));
        Assert.Throws<ReservationConflictException>(() => data.Store.Acquire(message with { OccurredAt = message.OccurredAt.AddSeconds(1) }));
        foreach (var items in new[] { new[] { new OrderLine("BOOK", 8, 2m) }, message.Items.Select(i => i with { UnitPrice = 9m }).ToArray() })
            Assert.Throws<ReservationConflictException>(() => data.Store.Acquire(message with { MessageId = Guid.NewGuid(), Items = items }));
        Assert.True(await data.Processor().ProcessAsync(message with { MessageId = Guid.NewGuid(), Items = [new("PEN", 3, 1.5m), new("BOOK", 2, 12.5m)] }, CancellationToken.None));
        Assert.Equal(2, data.Scalar("SELECT COUNT(*) FROM inventory_inbox"));
        Assert.Equal(1, data.Scalar("SELECT COUNT(*) FROM inventory_work"));
        Assert.Equal(2, data.Reserved("BOOK"));
    }

    [Fact]
    public async Task Failed_second_line_rolls_back_reservations_totals_and_inbox_then_recovers()
    {
        using var data = new InventoryData();
        var message = InventoryData.Message();
        Assert.False(data.Store.Acquire(message));
        Assert.Equal(1, data.Store.StartAttempt(message.OrderId));
        data.Execute("CREATE TRIGGER test_fail BEFORE INSERT ON inventory_reservations WHEN NEW.sku = 'PEN' BEGIN SELECT RAISE(ABORT, 'test failure'); END");
        Assert.Throws<SqliteException>(() => data.Store.Complete(message.OrderId));
        Assert.Equal(0, data.Scalar("SELECT COUNT(*) FROM inventory_stock"));
        Assert.Equal(0, data.Scalar("SELECT COUNT(*) FROM inventory_reservations"));
        Assert.Equal(0, data.Scalar("SELECT processed FROM inventory_inbox"));
        Assert.Equal(0, data.Scalar("SELECT completed FROM inventory_work"));
        Assert.Equal(1, data.Scalar("SELECT attempts FROM inventory_work"));
        data.Execute("DROP TRIGGER test_fail");
        var reopened = data.Reopen();
        var pending = Assert.Single(reopened.Pending());
        Assert.True(await data.Processor(store: reopened).ProcessAsync(pending, CancellationToken.None));
        Assert.Equal(2, data.Reserved("BOOK"));
        Assert.Equal(3, data.Reserved("PEN"));
        Assert.Equal(1, data.Scalar("SELECT processed FROM inventory_inbox"));
        Assert.Equal(2, data.Scalar("SELECT attempts FROM inventory_work"));
    }

    [Fact]
    public async Task Temporary_failures_and_interrupted_attempts_share_a_durable_budget_across_restart()
    {
        using var data = new InventoryData();
        var message = InventoryData.Message();
        Assert.False(data.Store.Acquire(message));
        Assert.Equal(1, data.Store.StartAttempt(message.OrderId)); // Interrupted before the reservation commit.
        var reopened = data.Reopen();
        Assert.Equal(message.MessageId, Assert.Single(reopened.Pending()).MessageId);
        Assert.True(await data.Processor(new("temporary-inventory-failure", 0, 2, message.OrderId), reopened).ProcessAsync(message, CancellationToken.None));
        Assert.Equal(3, data.Scalar("SELECT attempts FROM inventory_work"));
        Assert.Equal(DBNull.Value, data.Query("SELECT last_error FROM inventory_work"));
        Assert.Equal(2, data.Reserved("BOOK"));
    }

    [Fact]
    public async Task Exhausted_budget_is_not_reset_by_reopen_or_new_MessageId_and_needs_an_operator_cycle()
    {
        using var data = new InventoryData();
        var message = InventoryData.Message();
        Assert.False(await data.Processor(new("temporary-inventory-failure", 0, 3, message.OrderId)).ProcessAsync(message, CancellationToken.None));
        Assert.Equal(3, data.Scalar("SELECT attempts FROM inventory_work"));
        Assert.Equal("reservation_retry_exhausted", data.Query("SELECT last_error FROM inventory_work"));
        var reopened = data.Reopen();
        Assert.Empty(reopened.Pending());
        Assert.False(await data.Processor(store: reopened).ProcessAsync(message with { MessageId = Guid.NewGuid() }, CancellationToken.None));
        Assert.Equal(0, data.Scalar("SELECT COUNT(*) FROM inventory_reservations"));
        data.Execute("UPDATE inventory_work SET attempts = 0, last_error = NULL WHERE completed = 0");
        Assert.True(await data.Processor(store: reopened).ProcessAsync(Assert.Single(reopened.Pending()), CancellationToken.None));
        Assert.Equal(2, data.Reserved("BOOK"));
        Assert.Equal(2, data.Scalar("SELECT COUNT(*) FROM inventory_inbox WHERE processed = 1"));
    }

    [Fact]
    public async Task Cancellation_after_commit_leaves_durable_success_for_redelivery()
    {
        using var data = new InventoryData();
        var message = InventoryData.Message();
        using var stop = new CancellationTokenSource();
        var processing = data.Processor(new("happy-path", 60000, 0, message.OrderId)).ProcessAsync(message, stop.Token);
        Assert.Equal(1, data.Scalar("SELECT completed FROM inventory_work"));
        Assert.False(processing.IsCompleted);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        var reopened = data.Reopen();
        Assert.True(await data.Processor(store: reopened).ProcessAsync(message, CancellationToken.None));
        Assert.Equal(2, data.Reserved("BOOK"));
        Assert.Equal(1, data.Scalar("SELECT attempts FROM inventory_work"));
    }

    [Fact]
    public void Invalid_quantity_duplicates_metadata_and_UTF8_leave_no_work()
    {
        using var data = new InventoryData();
        var valid = InventoryData.Message();
        foreach (var items in new[] { new[] { new OrderLine("BOOK", 0, 1m) }, new[] { new OrderLine("BOOK", -2, 1m) }, new[] { new OrderLine("BOOK", 1, 1m), new OrderLine("BOOK", 2, 1m) } })
            Assert.Throws<ArgumentException>(() => data.Store.Acquire(valid with { Items = items }));
        var properties = InventoryData.Properties(valid);
        var body = Encoding.UTF8.GetBytes(WireJson.Write(valid));
        Assert.Equal(valid.MessageId, InventoryConsumer.Read(body, properties).MessageId);
        properties.MessageId = Guid.NewGuid().ToString();
        Assert.Throws<ArgumentException>(() => InventoryConsumer.Read(body, properties));
        Assert.Throws<DecoderFallbackException>(() => InventoryConsumer.Read(new byte[] { 0xff }, InventoryData.Properties(valid)));
        Assert.Throws<JsonException>(() => InventoryConsumer.Read(Encoding.UTF8.GetBytes("{}"), InventoryData.Properties(valid)));
        Assert.Equal(0, data.Scalar("SELECT COUNT(*) FROM inventory_work"));
        Assert.Equal(0, data.Scalar("SELECT COUNT(*) FROM inventory_stock"));
    }

    [Fact]
    public async Task Simulation_selection_affects_only_the_selected_order()
    {
        using var data = new InventoryData();
        var message = InventoryData.Message();
        Assert.True(await data.Processor(new("temporary-inventory-failure", 60000, 3, Guid.NewGuid())).ProcessAsync(message, CancellationToken.None));
        Assert.Equal(1, data.Scalar("SELECT attempts FROM inventory_work"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Simulation:OrderId"] = "invalid" }).Build();
        Assert.Throws<ArgumentException>(() => InventorySimulation.Read(config));
    }
}

internal sealed class InventoryData : IDisposable
{
    public LabSettings Settings { get; } = new("inventory-tests", Path.Combine(Path.GetTempPath(), "playground-inventory-" + Guid.NewGuid().ToString("N")), "127.0.0.1", 1, "unused", "unused");
    public InventoryStore Store { get; }
    public InventoryData() => Store = Reopen();
    public InventoryStore Reopen() { var store = new InventoryStore(Settings); store.Initialize(); return store; }
    public InventoryProcessor Processor(InventorySimulation? simulation = null, InventoryStore? store = null) => new(store ?? Store, simulation ?? new("happy-path", 0, 0, null), NullLogger<InventoryProcessor>.Instance);
    public static ReserveInventory Message() => new(1, Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), [new("BOOK", 2, 12.50m), new("PEN", 3, 1.50m)]);
    public static BasicProperties Properties(ReserveInventory message) => new() { MessageId = message.MessageId.ToString(), ContentType = "application/json", Type = "ReserveInventory.v1", Persistent = true };
    public object? Query(string sql) => OrderTestData.Query(Settings, "Inventory.Service", sql);
    public long Scalar(string sql) => Convert.ToInt64(Query(sql));
    public long Reserved(string sku)
    {
        using var connection = LocalStore.Open(Store.Database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT reserved FROM inventory_stock WHERE sku = $sku";
        command.Parameters.AddWithValue("$sku", sku);
        return Convert.ToInt64(command.ExecuteScalar());
    }
    public void Execute(string sql) => Query(sql);
    public void Dispose()
    {
        using var connection = LocalStore.Open(Store.Database);
        SqliteConnection.ClearPool(connection);
        if (Directory.Exists(Settings.Root)) Directory.Delete(Settings.Root, recursive: true);
    }
}
