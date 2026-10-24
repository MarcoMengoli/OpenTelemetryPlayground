using Microsoft.Data.Sqlite;
using Playground.Contracts;

namespace Fulfillment.Service;

public static class FulfillmentEndpoints
{
    public static void MapFulfillment(this WebApplication app) =>
        app.MapGet("/internal/v1/warehouse-requests/{warehouseReference}", (string warehouseReference, FulfillmentStore store, ILogger<FulfillmentStore> logger) =>
        {
            try
            {
                var mapping = store.Lookup(warehouseReference);
                return mapping is null
                    ? Results.Json(new ApiError("not_found", "Unknown warehouse reference."), WireJson.Options, statusCode: 404)
                    : Results.Json(mapping, WireJson.Options);
            }
            catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6)
            {
                logger.LogWarning("Fulfillment lookup database temporarily busy");
                return Results.Json(new ApiError("temporarily_unavailable", "Lookup temporarily unavailable."), WireJson.Options, statusCode: 503);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Fulfillment lookup failed");
                return Results.Json(new ApiError("internal_error", "Lookup failed."), WireJson.Options, statusCode: 500);
            }
        });
}