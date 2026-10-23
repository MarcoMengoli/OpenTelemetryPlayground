using Playground.Infrastructure;

var builder = FoundationHost.Create(args, "Inventory.Service");
var app = builder.Build();
app.MapFoundation("Inventory.Service");
// Business consumers and file polling are implemented in subsequent stories.
await app.RunAsync();
