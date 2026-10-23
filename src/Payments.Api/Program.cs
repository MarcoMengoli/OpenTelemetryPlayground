using Playground.Infrastructure;

var builder = FoundationHost.Create(args, "Payments.Api");
var app = builder.Build();
app.MapFoundation("Payments.Api");
app.MapPost("/api/v1/authorizations", () => FoundationHost.NotImplemented());
app.MapPost("/api/v1/captures", () => FoundationHost.NotImplemented());
app.MapPost("/api/v1/authorization-cancellations", () => FoundationHost.NotImplemented());
app.MapPost("/api/v1/invoices", () => FoundationHost.NotImplemented());
app.MapGet("/api/v1/payments/{orderId:guid}", () => FoundationHost.NotImplemented());
await app.RunAsync();

