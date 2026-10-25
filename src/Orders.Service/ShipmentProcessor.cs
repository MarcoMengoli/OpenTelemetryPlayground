using Playground.Contracts;

namespace Orders.Service;

public sealed class ShipmentProcessor(OrdersStore store, ShipmentMapping mapping, PaymentCompletion payments, ILogger<ShipmentProcessor> logger)
{
    private readonly SemaphoreSlim processing = new(1, 1);

    public async Task<bool> ProcessAsync(ShipmentConfirmation message, CancellationToken token)
    {
        // The laboratory has one instance and prefetch 1; preserve that ordering
        // for concurrent callers as well, without spending a duplicate's budget.
        await processing.WaitAsync(token);
        try { return await ProcessOneAsync(message, token); }
        finally { processing.Release(); }
    }

    private async Task<bool> ProcessOneAsync(ShipmentConfirmation message, CancellationToken token)
    {
        if (!store.AcceptShipment(message)) return true;
        while (!store.ShipmentBound(message.MessageId))
        {
            token.ThrowIfCancellationRequested();
            var attempt = store.StartMappingAttempt(message.MessageId);
            if (attempt == 0) return false;
            try { await mapping.VerifyAsync(message, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (PaymentCallException exception) when (!exception.Retryable)
            {
                store.MappingFailed(message, exception.Code, true);
                logger.LogError("Shipment mapping rejected for {OrderId}: {Error}", message.OrderId, exception.Code);
                return false;
            }
            catch (Exception exception) when (Temporary(exception))
            {
                store.MappingFailed(message, attempt == 3 ? "shipment_mapping_retry_exhausted" : "shipment_mapping_unavailable", false);
                logger.LogWarning("Shipment mapping failed for {OrderId}, attempt {Attempt}", message.OrderId, attempt);
                await Pause(attempt, token);
                continue;
            }
            store.BindShipment(message);
        }
        var work = store.Completion(message.OrderId);
        while (work.Status != OrderStatus.Completed)
        {
            token.ThrowIfCancellationRequested();
            var operation = work.Status == OrderStatus.Shipped ? CompletionOperation.Capture : CompletionOperation.Invoice;
            var attempt = store.StartCompletionAttempt(work.OrderId, operation);
            if (attempt == 0) return false;
            try
            {
                if (operation == CompletionOperation.Capture) store.RecordCapture(await payments.CaptureAsync(work, token));
                else store.RecordInvoice(await payments.InvoiceAsync(work, token));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (PaymentCallException exception) when (!exception.Retryable)
            {
                store.CompletionFailed(work.OrderId, operation, operation.ToString().ToLowerInvariant() + "_" + exception.Code, true);
                logger.LogError("Order {OrderId}: {Operation} blocked: {Error}", work.OrderId, operation, exception.Code);
                return false;
            }
            catch (Exception exception) when (Temporary(exception))
            {
                store.CompletionFailed(work.OrderId, operation, operation.ToString().ToLowerInvariant() + (attempt == 3 ? "_retry_exhausted" : "_temporarily_unavailable"), false);
                logger.LogWarning("Order {OrderId}: {Operation} failed, attempt {Attempt}", work.OrderId, operation, attempt);
                await Pause(attempt, token);
                continue;
            }
            work = store.Completion(message.OrderId);
            logger.LogInformation("Order {OrderId}: {Status}", work.OrderId, work.Status);
        }
        return true;
    }

    private static bool Temporary(Exception exception) => exception is HttpRequestException or OperationCanceledException || exception is PaymentCallException { Retryable: true };
    private static Task Pause(int attempt, CancellationToken token) => Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), token);
}
