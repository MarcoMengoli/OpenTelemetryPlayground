using Playground.Infrastructure;

var builder = FoundationHost.Create(args, "Warehouse.Simulator");
var app = builder.Build();
app.MapFoundation("Warehouse.Simulator");
// Business consumers and file polling are implemented in subsequent stories.
await app.RunAsync();
