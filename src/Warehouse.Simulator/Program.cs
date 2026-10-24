using Playground.Infrastructure;
using Warehouse.Simulator;

var builder = FoundationHost.Create(args, "Warehouse.Simulator");
builder.Services.AddSingleton(WarehouseSimulation.Read(builder.Configuration));
builder.Services.AddSingleton<SimulatorStore>();
builder.Services.AddSingleton<WarehouseProcessor>();
builder.Services.AddHostedService<WarehouseWorker>();
var app = builder.Build();
app.Services.GetRequiredService<SimulatorStore>().Initialize();
app.MapFoundation("Warehouse.Simulator");
await app.RunAsync();