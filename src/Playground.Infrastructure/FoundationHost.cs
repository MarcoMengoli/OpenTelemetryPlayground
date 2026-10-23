using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Playground.Contracts;
using RabbitMQ.Client;
using Serilog;

namespace Playground.Infrastructure;

public static class FoundationHost
{
    public static WebApplicationBuilder Create(string[] args, string service)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSerilog(configuration => configuration.MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            .Enrich.WithProperty("Service", service)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Service} {Message:lj}{NewLine}{Exception}"));
        builder.Services.AddSingleton(LabSettings.Read(builder.Configuration));
        builder.Services.AddSingleton(provider => new FoundationLifecycle(service,
            provider.GetRequiredService<LabSettings>(), provider.GetRequiredService<ILogger<FoundationLifecycle>>(), provider.GetRequiredService<IHostApplicationLifetime>()));
        builder.Services.AddHostedService(provider => provider.GetRequiredService<FoundationLifecycle>());
        return builder;
    }
    public static void MapFoundation(this WebApplication app, string service)
    {
        app.MapGet("/health/ready", async (FoundationLifecycle lifecycle, CancellationToken token) =>
            await lifecycle.IsReadyAsync(token) ? Results.Ok(new { service, status = "ready", scope = "foundation" })
                : Results.Json(new ApiError("not_ready", "Infrastructure is unavailable."), statusCode: 503));
    }
    public static IResult NotImplemented() => Results.Json(new ApiError("not_implemented", "US02 provides infrastructure and contracts only."), WireJson.Options, statusCode: 501);
}

public sealed class FoundationLifecycle(string service, LabSettings settings, ILogger<FoundationLifecycle> logger, IHostApplicationLifetime lifetime) : IHostedService, IAsyncDisposable
{
    private IConnection? connection;
    private readonly CancellationTokenSource stopWatching = new();
    private Task? stopWatcher;
    private int disposed;
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LocalStore.InitializeAsync(settings.Database(service), cancellationToken);
            foreach (var folder in new[] { "requests", "responses", "processed", "errors" })
            {
                var directory = Path.Combine(settings.Warehouse, folder);
                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, $".probe-{Guid.NewGuid():N}");
                await File.WriteAllTextAsync(probe, service, cancellationToken);
                File.Delete(probe);
            }
            connection = await Broker.Factory(settings).CreateConnectionAsync(cancellationToken);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
            await Broker.DeclareAsync(channel, settings, cancellationToken);
            var stopFile = Path.Combine(settings.Root, "run", service + ".stop");
            Directory.CreateDirectory(Path.GetDirectoryName(stopFile)!);
            File.Delete(stopFile);
            stopWatcher = Task.Run(async () =>
            {
                try
                {
                    while (!stopWatching.IsCancellationRequested)
                    {
                        if (File.Exists(stopFile)) { lifetime.StopApplication(); return; }
                        await Task.Delay(250, stopWatching.Token);
                    }
                }
                catch (OperationCanceledException) when (stopWatching.IsCancellationRequested) { }
            }, CancellationToken.None);
            logger.LogInformation("Host started in {LabName}; database {Database}; broker topology ready", settings.Name, settings.Database(service));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Infrastructure startup failed in {LabName}", settings.Name);
            throw;
        }
    }
    public async Task<bool> IsReadyAsync(CancellationToken token)
    {
        try { return connection?.IsOpen == true && await LocalStore.ReadStartsAsync(settings.Database(service), token) > 0; }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Readiness failed in {LabName}", settings.Name);
            return false;
        }
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Host stopping in {LabName}", settings.Name);
        await stopWatching.CancelAsync();
        if (stopWatcher is not null) await stopWatcher;
        if (connection is { IsOpen: true }) await connection.CloseAsync(cancellationToken);
        logger.LogInformation("Host stopped in {LabName}", settings.Name);
    }
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; await stopWatching.CancelAsync(); if (stopWatcher is not null) await stopWatcher; stopWatching.Dispose(); if (connection is not null) await connection.DisposeAsync(); }
}
