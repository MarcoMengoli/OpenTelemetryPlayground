using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Playground.Contracts;
using Playground.Infrastructure;
using Response = Playground.Contracts.WarehouseResponse;

namespace Playground.WarehouseResponses;

public sealed class ResponseReader(LabSettings settings, ResponseStore store, ILogger<ResponseReader> logger)
{
    public void Scan(DateTimeOffset now)
    {
        var directory = Path.Combine(settings.Warehouse, "responses");
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (!store.CanReadFile(name, now)) continue;
            try
            {
                string? destination = null;
                try
                {
                    var response = WireJson.Read<Response>(WarehouseFiles.Read(file));
                    var acquired = store.Accept(response);
                    if (acquired.State == "Ready") destination = "processed";
                    if (acquired.State == "Rejected")
                    {
                        destination = "errors";
                        logger.LogWarning("Quarantining response {WarehouseReference}: {Reason}", response.WarehouseReference, acquired.LastError);
                    }
                }
                catch (Exception e) when (e is JsonException or ArgumentException or DecoderFallbackException or ResponseConflictException)
                {
                    destination = "errors";
                    logger.LogWarning(e, "Invalid or conflicting response file {FileName}", name);
                }
                // Pending lookup stays in place, but its validated business payload is already durable.
                if (destination is not null) WarehouseFiles.Archive(file, Path.Combine(settings.Warehouse, destination, "responses"));
                store.ClearFileFailure(name);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                var attempt = store.FailFile(name, now);
                logger.LogError(e, "Response file {FileName} inaccessible; attempt {Attempt}/3. Input retained; operator recovery required after exhaustion", name, attempt);
            }
        }
    }
}

public sealed class ResponseLookup(ResponseStore store, HttpClient client, ILogger<ResponseLookup> logger)
{
    public async Task ResumePendingAsync(DateTimeOffset now, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        foreach (var acquired in store.Pending())
        {
            token.ThrowIfCancellationRequested();
            var response = acquired.Response;
            if (acquired.Attempts >= 3)
            {
                store.FailLookup(response.WarehouseReference, acquired.Attempts, "lookup_retry_exhausted", true, now);
                logger.LogError("Lookup budget exhausted for {WarehouseReference}; response remains durable", response.WarehouseReference);
                continue;
            }
            var attemptTime = now + Stopwatch.GetElapsedTime(started);
            if (acquired.RetryAfter > attemptTime) continue;
            var attempt = store.StartAttempt(response.WarehouseReference, attemptTime);
            if (attempt == 0) continue;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                using var result = await client.GetAsync("/internal/v1/warehouse-requests/" + Uri.EscapeDataString(response.WarehouseReference), deadline.Token);
                if (!result.IsSuccessStatusCode)
                {
                    var transient = result.StatusCode == HttpStatusCode.NotFound || (int)result.StatusCode >= 500 ||
                        result.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
                    Fail("lookup_http_" + (int)result.StatusCode, !transient);
                    continue;
                }
                var mapping = WireJson.Read<WarehouseMappingResponse>(await result.Content.ReadAsStringAsync(deadline.Token));
                store.Complete(response, mapping);
                logger.LogInformation("Response {WarehouseReference} resolved to order {OrderId}; confirmation is durable", response.WarehouseReference, mapping.OrderId);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
            { Fail("lookup_temporarily_unavailable", false); }
            catch (Exception e) when (e is JsonException or ArgumentException or ResponseConflictException)
            { Fail("lookup_invalid_response", true); }

            void Fail(string error, bool permanent)
            {
                store.FailLookup(response.WarehouseReference, attempt, error, permanent, now + Stopwatch.GetElapsedTime(started));
                logger.LogWarning("Lookup failed for {WarehouseReference}, attempt {Attempt}/3: {Reason}", response.WarehouseReference, attempt, error);
            }
        }
    }
}

public sealed class ResponseWorker(LabSettings settings, ResponseReader reader, ResponseLookup lookup, ILogger<ResponseWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var directory = Path.Combine(settings.Warehouse, "responses");
        Directory.CreateDirectory(directory);
        // Separate from the simulator's writer lease. Never remove its temporary files.
        using var lease = new FileStream(Path.Combine(directory, ".response-reader.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                reader.Scan(DateTimeOffset.UtcNow);
                await lookup.ResumePendingAsync(DateTimeOffset.UtcNow, stoppingToken);
                reader.Scan(DateTimeOffset.UtcNow);
                failures = 0;
                await Task.Delay(500, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                logger.LogError(e, "Response polling failed, attempt {Attempt}/3; restart required after exhaustion", ++failures);
                if (failures >= 3) return;
                await Task.Delay(TimeSpan.FromSeconds(1 << (failures - 1)), stoppingToken);
            }
        }
    }
}
