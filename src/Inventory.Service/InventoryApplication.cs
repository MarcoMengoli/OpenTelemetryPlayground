using Playground.Infrastructure;

namespace Inventory.Service;

public static class InventoryApplication
{
    public static WebApplication Build(string[] args)
    {
        var builder = FoundationHost.Create(args, "Inventory.Service");
        builder.Services.AddSingleton(InventorySimulation.Read(builder.Configuration));
        builder.Services.AddSingleton<InventoryStore>();
        builder.Services.AddSingleton<InventoryProcessor>();
        builder.Services.AddHostedService<InventoryConsumer>();
        var app = builder.Build();
        app.Services.GetRequiredService<InventoryStore>().Initialize();
        app.MapFoundation("Inventory.Service");
        return app;
    }
}
