using Playground.Infrastructure;

var builder = FoundationHost.Create(args, "Couriers.Api");
var app = builder.Build();
app.MapFoundation("Couriers.Api");
app.MapPost("/api/v1/estimates", () => FoundationHost.NotImplemented());
await app.RunAsync();
