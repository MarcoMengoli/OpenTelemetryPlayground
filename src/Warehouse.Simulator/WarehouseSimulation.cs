namespace Warehouse.Simulator;

public sealed record WarehouseSimulation(string Profile, int DelayMs, string? WarehouseReference)
{
    public bool Selects(string reference) => WarehouseReference is null || WarehouseReference == reference;
    public static WarehouseSimulation Read(IConfiguration config)
    {
        var profile = config["Simulation:Profile"] ?? "happy-path";
        var delay = config.GetValue("Simulation:DelayMs", profile == "slow-warehouse" ? 6000 : 0);
        var reference = config["Simulation:WarehouseReference"];
        if (delay < 0 || (reference is not null && string.IsNullOrWhiteSpace(reference))) throw new ArgumentException("Invalid warehouse simulation settings.");
        return new(profile, delay, reference);
    }
}