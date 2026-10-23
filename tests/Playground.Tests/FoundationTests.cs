using System.Text;
using Microsoft.Extensions.Configuration;
using Playground.Contracts;
using Playground.Infrastructure;
using RabbitMQ.Client;
using Xunit;

namespace Playground.Tests;

public sealed class ContractTests
{
    [Fact]
    public void Published_examples_roundtrip_and_invalid_examples_are_rejected()
    {
        var types = typeof(IContract).Assembly.GetTypes().Where(t => t.IsClass && typeof(IContract).IsAssignableFrom(t));
        foreach (var type in types)
        {
            var method = typeof(ContractTests).GetMethod(nameof(Verify), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.MakeGenericMethod(type);
            method.Invoke(null, null);
        }
    }
    private static void Verify<T>() where T : IContract
    {
        var path = Path.Combine(AppContext.BaseDirectory, "examples", typeof(T).Name + ".valid.json");
        var value = WireJson.Read<T>(File.ReadAllText(path));
        var serialized = WireJson.Write(value);
        Assert.Equal(serialized, WireJson.Write(WireJson.Read<T>(serialized)));
        Assert.ThrowsAny<Exception>(() => WireJson.Read<T>("{}"));
        Assert.ThrowsAny<Exception>(() => WireJson.Read<T>("null"));
        Assert.ThrowsAny<Exception>(() => WireJson.Read<T>(serialized.Insert(1, "\"unexpected\":1,")));
        foreach (var invalid in Directory.GetFiles(Path.GetDirectoryName(path)!, typeof(T).Name + ".invalid*.json"))
            Assert.ThrowsAny<Exception>(() => WireJson.Read<T>(File.ReadAllText(invalid)));
    }
    [Theory]
    [InlineData("../other")]
    [InlineData("x/y")]
    [InlineData("UPPER")]
    [InlineData("")]
    public void Environment_name_cannot_escape_its_resources(string name)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Lab:Name"] = name }).Build();
        Assert.Throws<ArgumentException>(() => LabSettings.Read(config));
    }
}

public sealed class PersistenceTests
{
    [Fact]
    public async Task SQLite_survives_reopen_and_services_are_separate()
    {
        var root = Path.Combine(Path.GetTempPath(), "playground-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = Path.Combine(root, "orders", "state.db");
            var second = Path.Combine(root, "payments", "state.db");
            await LocalStore.InitializeAsync(first);
            await LocalStore.InitializeAsync(first);
            await LocalStore.InitializeAsync(second);
            Assert.Equal(2, await LocalStore.ReadStartsAsync(first));
            Assert.Equal(1, await LocalStore.ReadStartsAsync(second));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}

public sealed class BrokerFactAttribute : FactAttribute
{
    public BrokerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PLAYGROUND_BROKER_TEST") != "1")
            Skip = "Real broker test: run scripts/lab.ps1 verify against a running isolated lab.";
    }
}

public sealed class BrokerTests
{
    [BrokerFact]
    [Trait("Category", "Broker")]
    public async Task Real_broker_confirms_routes_redelivers_and_acknowledges_business_message()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var settings = LabSettings.Read(new ConfigurationBuilder().AddEnvironmentVariables().Build());
        await using var connection = await Broker.Factory(settings).CreateConnectionAsync(token);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), token);
        var exchange = $"{settings.Name}.test.{Guid.NewGuid():N}";
        var queue = exchange + ".orders";
        try
        {
            await channel.ExchangeDeclareAsync(exchange, ExchangeType.Direct, durable: false, autoDelete: false, cancellationToken: token);
            await channel.QueueDeclareAsync(queue, durable: false, exclusive: true, autoDelete: true, cancellationToken: token);
            await channel.QueueBindAsync(queue, exchange, "order.created.v1", cancellationToken: token);
            var message = new OrderCreated(1, Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(),
                [new("BOOK", 1, 12.50m)], new("Ada", "Via Roma 1", "Bologna", "40100", "IT"));
            var properties = new BasicProperties { MessageId = message.MessageId.ToString(), ContentType = "application/json", Persistent = true, Type = "OrderCreated.v1" };
            await channel.BasicPublishAsync(exchange, "order.created.v1", mandatory: true, properties, Encoding.UTF8.GetBytes(WireJson.Write(message)), token);
            var first = await channel.BasicGetAsync(queue, autoAck: false, token);
            Assert.NotNull(first);
            Assert.Equal(message.OrderId, WireJson.Read<OrderCreated>(Encoding.UTF8.GetString(first.Body.Span)).OrderId);
            Assert.Equal(message.MessageId.ToString(), first.BasicProperties.MessageId);
            // Reject once with requeue to prove the broker retains an unacknowledged delivery.
            await channel.BasicNackAsync(first.DeliveryTag, multiple: false, requeue: true, token);
            var second = await channel.BasicGetAsync(queue, autoAck: false, token);
            Assert.NotNull(second);
            Assert.True(second.Redelivered);
            Assert.Equal(message.MessageId, WireJson.Read<OrderCreated>(Encoding.UTF8.GetString(second.Body.Span)).MessageId);
            await channel.BasicAckAsync(second.DeliveryTag, multiple: false, token);
            Assert.Null(await channel.BasicGetAsync(queue, autoAck: false, token));
            // Mandatory return must fail publishing instead of silently losing an unroutable message.
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await channel.BasicPublishAsync(exchange, "unroutable", true, properties, Encoding.UTF8.GetBytes(WireJson.Write(message)), token));
        }
        finally
        {
            if (channel.IsOpen)
            {
                await channel.QueueDeleteAsync(queue, cancellationToken: CancellationToken.None);
                await channel.ExchangeDeleteAsync(exchange, cancellationToken: CancellationToken.None);
            }
        }
    }
}

