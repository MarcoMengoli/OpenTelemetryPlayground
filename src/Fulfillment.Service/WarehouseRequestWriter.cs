using Playground.Contracts;
using Playground.Infrastructure;

namespace Fulfillment.Service;

public sealed class WarehouseRequestWriter(FulfillmentStore store, LabSettings settings, ILogger<WarehouseRequestWriter> logger)
{
    public void WritePending(Action<FilePublicationStage>? checkpoint = null)
    {
        foreach (var entry in store.UnsentFiles())
        {
            if (!store.CanTryFile(entry.WarehouseReference)) continue;
            try
            {
                // Revalidate the persisted hand-off; map/order data never enters the file.
                var request = WireJson.Read<WarehouseRequest>(entry.PayloadJson);
                WarehouseFiles.Publish(Path.Combine(settings.Warehouse, "requests"),
                    WarehouseFiles.Name("request", request.WarehouseReference), WireJson.Write(request), checkpoint);
                store.MarkFileSent(entry.WarehouseReference);
                logger.LogInformation("Warehouse request published for {WarehouseReference}", entry.WarehouseReference);
            }
            catch (WarehouseFileConflictException e)
            {
                store.RecordFileFailure(entry.WarehouseReference, "file_conflict", terminal: true);
                logger.LogError(e, "Warehouse request output conflicts for {WarehouseReference}", entry.WarehouseReference);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                store.RecordFileFailure(entry.WarehouseReference, "file_io", terminal: false);
                logger.LogWarning(e, "Warehouse request retained for bounded retry: {WarehouseReference}", entry.WarehouseReference);
            }
        }
    }
}

public sealed class WarehouseRequestWorker(WarehouseRequestWriter writer, LabSettings settings, ILogger<WarehouseRequestWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        var directory = Path.Combine(settings.Warehouse, "requests");
        using var lease = WarehouseFiles.AcquireWriterLease(directory, "request");
        WarehouseFiles.RemoveOrphanTemps(directory, "request");
        while (!token.IsCancellationRequested)
        {
            try { writer.WritePending(); }
            catch (Exception e) { logger.LogError(e, "Warehouse writer cycle failed; durable outbox retained"); }
            try { await Task.Delay(500, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }
}