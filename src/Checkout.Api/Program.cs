using Playground.Infrastructure;

var builder = FoundationHost.Create(args, "Checkout.Api");
var app = builder.Build();
app.MapFoundation("Checkout.Api");
app.MapPost("/api/v1/orders", () => FoundationHost.NotImplemented());
app.MapGet("/api/v1/orders/{orderId:guid}", () => FoundationHost.NotImplemented());
await app.RunAsync();
