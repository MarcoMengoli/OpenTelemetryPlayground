using Playground.Infrastructure;

var builder = FoundationHost.Create(args, "Fulfillment.Service");
var app = builder.Build();
app.MapFoundation("Fulfillment.Service");
app.MapGet("/internal/v1/warehouse-requests/{warehouseReference}", () => FoundationHost.NotImplemented());
await app.RunAsync();
