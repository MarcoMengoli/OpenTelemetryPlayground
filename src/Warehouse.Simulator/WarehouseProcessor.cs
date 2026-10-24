using System.Text;
using System.Text.Json;
using Playground.Contracts;
using Playground.Infrastructure;

namespace Warehouse.Simulator;

public enum WarehouseInputStage { Accepted, Archived }

public sealed class WarehouseProcessor(SimulatorStore store, LabSettings settings, WarehouseSimulation simulation, ILogger<WarehouseProcessor> logger)
{
    public void ReadRequests(DateTimeOffset now, Action<WarehouseInputStage>? checkpoint = null)
    {
        var directory = Path.Combine(settings.Warehouse, "requests");
        Directory.CreateDirectory(directory);
        foreach (var path in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var key = "input:" + Path.GetFileName(path);
            if (!store.CanTry(key, now)) continue;
            try
            {
                WarehouseRequest request;
                try
                {
                    request = WireJson.Read<WarehouseRequest>(WarehouseFiles.Read(path));
                    store.Accept(request, now);
                }
                catch (Exception e) when (e is JsonException or ArgumentException or DecoderFallbackException or WarehouseRequestConflictException)
                {
                    var destination = WarehouseFiles.Archive(path, Path.Combine(settings.Warehouse, "errors", "requests"));
                    store.ClearFailure(key);
                    logger.LogWarning("Warehouse request quarantined at {File}: {Reason}", destination, e.Message);
                    continue;
                }
                checkpoint?.Invoke(WarehouseInputStage.Accepted);
                WarehouseFiles.Archive(path, Path.Combine(settings.Warehouse, "processed", "requests"));
                store.ClearFailure(key);
                checkpoint?.Invoke(WarehouseInputStage.Archived);
                logger.LogInformation("Warehouse request acquired for {WarehouseReference}", request.WarehouseReference);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                store.Fail(key, "input_io", terminal: false, now);
                logger.LogError(e, "Warehouse input retained after I/O error for {File}; attempts {Attempts}", path, store.Failure(key)!.Attempts);
            }
        }
    }
    public void WriteResponses(DateTimeOffset now, Action<FilePublicationStage>? checkpoint = null)
    {
        foreach (var shipment in store.Pending())
        {
            var selected = simulation.Selects(shipment.Reference);
            if (selected && simulation.Profile == "missing-warehouse-response") continue;
            if (selected && simulation.Profile == "slow-warehouse" && now < shipment.AcceptedAt.AddMilliseconds(simulation.DelayMs)) continue;
            var key = "output:" + shipment.Reference;
            if (!store.CanTry(key, now)) continue;
            try
            {
                var response = WireJson.Read<WarehouseResponse>(shipment.ResponseJson);
                var json = WireJson.Write(response);
                var directory = Path.Combine(settings.Warehouse, "responses");
                WarehouseFiles.Publish(directory, WarehouseFiles.Name("response", shipment.Reference), json, checkpoint);
                if (selected && simulation.Profile == "duplicate-warehouse-response")
                    WarehouseFiles.Publish(directory, WarehouseFiles.Name("response", shipment.Reference, duplicate: true), json, checkpoint);
                store.MarkSent(shipment.Reference);
                logger.LogInformation("Warehouse response published for {WarehouseReference}, shipment {ShipmentId}", shipment.Reference, response.ShipmentId);
            }
            catch (WarehouseFileConflictException e)
            {
                store.Fail(key, "output_conflict", terminal: true, now);
                logger.LogError(e, "Warehouse response blocked for {WarehouseReference}", shipment.Reference);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                store.Fail(key, "output_io", terminal: false, now);
                logger.LogError(e, "Warehouse response retained after I/O error for {WarehouseReference}; attempts {Attempts}", shipment.Reference, store.Failure(key)!.Attempts);
            }
        }
    }
}

public sealed class WarehouseWorker(WarehouseProcessor processor, LabSettings settings, ILogger<WarehouseWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        var directory = Path.Combine(settings.Warehouse, "responses");
        using var lease = WarehouseFiles.AcquireWriterLease(directory, "response");
        WarehouseFiles.RemoveOrphanTemps(directory, "response");
        while (!token.IsCancellationRequested)
        {
            try { processor.ReadRequests(DateTimeOffset.UtcNow); }
            catch (Exception e) { logger.LogError(e, "Warehouse input cycle failed; inputs retained"); }
            // Failure of input enumeration must not starve an already committed response.
            try { processor.WriteResponses(DateTimeOffset.UtcNow); }
            catch (Exception e) { logger.LogError(e, "Warehouse output cycle failed; durable responses retained"); }
            try { await Task.Delay(500, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }
}