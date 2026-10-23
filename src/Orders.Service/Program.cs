using Playground.Infrastructure;

var builder = FoundationHost.Create(args, "Orders.Service");
var app = builder.Build();
app.MapFoundation("Orders.Service");
app.MapGet("/internal/v1/orders/{orderId:guid}", () => FoundationHost.NotImplemented());
await app.RunAsync();
