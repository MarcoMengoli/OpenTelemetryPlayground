using Playground.Infrastructure;
using Payments.Api;

var builder = FoundationHost.Create(args, "Payments.Api");
builder.Services.AddSingleton(PaymentSimulation.Read(builder.Configuration));
builder.Services.AddSingleton<PaymentStore>();
var app = builder.Build();
app.Services.GetRequiredService<PaymentStore>().Initialize();
app.MapFoundation("Payments.Api");
app.MapPayments();
await app.RunAsync();
