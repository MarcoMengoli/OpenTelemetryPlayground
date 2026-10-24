using Couriers.Api;
using Playground.Infrastructure;

var builder = FoundationHost.Create(args, "Couriers.Api");
builder.Services.AddSingleton(CourierSimulation.Read(builder.Configuration));
builder.Services.AddSingleton<CourierStore>();
var app = builder.Build();
app.Services.GetRequiredService<CourierStore>().Initialize();
app.MapFoundation("Couriers.Api");
app.MapCouriers();
await app.RunAsync();