using Microsoft.Data.Sqlite;
using Playground.Contracts;

namespace Inventory.Service;

public sealed class InventoryProcessor(InventoryStore store, InventorySimulation simulation, ILogger<InventoryProcessor> logger)
{
    private readonly SemaphoreSlim processing = new(1, 1);

    public async Task<bool> ProcessAsync(ReserveInventory message, CancellationToken token)
    {
        // One instance owns this service, with prefetch 1. Keep the same ordering
        // for concurrent callers so duplicates cannot spend each other's budget.
        await processing.WaitAsync(token);
        try { return await ProcessOneAsync(message, token); }
        finally { processing.Release(); }
    }

    private async Task<bool> ProcessOneAsync(ReserveInventory message, CancellationToken token)
    {
        var storageFailures = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var attempt = 0;
            try
            {
                if (store.Acquire(message)) return true;
                attempt = store.StartAttempt(message.OrderId);
                if (attempt == 0) { store.Failed(message.OrderId, 3); return false; }
                if (simulation.Fails(message.OrderId, attempt))
                {
                    store.Failed(message.OrderId, attempt);
                    logger.LogWarning("Simulated reservation failure for {OrderId}, attempt {Attempt}", message.OrderId, attempt);
                    await Pause(attempt, token);
                    continue;
                }
                store.Complete(message.OrderId);
                logger.LogInformation("Inventory reserved for {OrderId}", message.OrderId);
                // The delay exposes the commit/ack boundary without holding a DB transaction.
                if (simulation.Selects(message.OrderId) && simulation.DelayMs > 0) await Task.Delay(simulation.DelayMs, token);
                return true;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6 or 10 or 14)
            {
                logger.LogWarning(exception, "Inventory storage failed for {OrderId}, attempt {Attempt}", message.OrderId, attempt);
                if (attempt > 0)
                {
                    try { store.Failed(message.OrderId, attempt); }
                    catch (SqliteException failure) { logger.LogError(failure, "Could not record reservation error for {OrderId}; retry budget remains durable", message.OrderId); }
                }
                // When storage cannot even persist acquisition/attempts, bound the
                // local retries too; never turn a nack into an endless hot requeue.
                if (++storageFailures >= 3) return false;
                await Pause(attempt > 0 ? attempt : storageFailures, token);
            }
        }
    }

    private static Task Pause(int attempt, CancellationToken token) => Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), token);
}
