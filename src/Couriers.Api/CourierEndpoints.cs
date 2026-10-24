using System.Text.Json;
using Microsoft.Data.Sqlite;
using Playground.Contracts;

namespace Couriers.Api;

public static class CourierEndpoints
{
    public static void MapCouriers(this WebApplication app) =>
        app.MapPost("/api/v1/estimates", async (HttpRequest request, CourierStore store,
            CourierSimulation simulation, ILogger<CourierStore> logger) =>
        {
            DeliveryEstimateRequest input;
            try
            {
                if (!request.HasJsonContentType()) return Error(400, "invalid_request");
                input = await JsonSerializer.DeserializeAsync<DeliveryEstimateRequest>(request.Body, WireJson.Options, request.HttpContext.RequestAborted)
                    ?? throw new JsonException("Null request.");
                input.Validate();
            }
            catch (Exception e) when (e is JsonException or ArgumentException or BadHttpRequestException)
            { return Error(400, "invalid_request"); }
            try
            {
                var (response, attempt) = store.Estimate(input);
                if (simulation.Selects(input.OrderId))
                {
                    if (simulation.Profile == "temporary-courier-failure" && attempt <= simulation.FailuresBeforeSuccess)
                    {
                        logger.LogWarning("Simulated courier failure for {OrderId}, attempt {Attempt}", input.OrderId, attempt);
                        return Error(503, "temporarily_unavailable");
                    }
                    if (simulation.Profile == "slow-courier" && simulation.DelayMs > 0)
                        await Task.Delay(simulation.DelayMs, request.HttpContext.RequestAborted);
                }
                logger.LogInformation("Courier estimate available for {OrderId}, attempt {Attempt}", input.OrderId, attempt);
                return Results.Json(response, WireJson.Options);
            }
            catch (CourierConflictException) { return Error(409, "idempotency_conflict"); }
            catch (OperationCanceledException) when (request.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
            catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6)
            {
                logger.LogWarning("Courier database temporarily busy");
                return Error(503, "temporarily_unavailable");
            }
            catch (Exception e)
            {
                logger.LogError(e, "Courier estimate failed");
                return Error(500, "internal_error");
            }
        });
    private static IResult Error(int status, string code) =>
        Results.Json(new ApiError(code, "Courier estimate could not be completed."), WireJson.Options, statusCode: status);
}