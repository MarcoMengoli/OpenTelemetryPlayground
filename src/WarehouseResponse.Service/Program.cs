using Playground.Infrastructure;

var builder = FoundationHost.Create(args, "WarehouseResponse.Service");
var app = builder.Build();
app.MapFoundation("WarehouseResponse.Service");
// Business consumers and file polling are implemented in subsequent stories.
await app.RunAsync();
