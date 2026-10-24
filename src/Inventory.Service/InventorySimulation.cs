namespace Inventory.Service;

public sealed record InventorySimulation(string Profile, int DelayMs, int FailuresBeforeSuccess, Guid? OrderId)
{
    public static InventorySimulation Read(IConfiguration configuration)
    {
        var profile = configuration["Simulation:Profile"] ?? "happy-path";
        var delay = configuration.GetValue<int>("Simulation:DelayMs");
        var failures = configuration.GetValue<int>("Simulation:FailuresBeforeSuccess");
        if (profile is not ("happy-path" or "temporary-inventory-failure") || delay is < 0 or > 60000 || failures is < 0 or > 1000)
            throw new ArgumentException("Invalid Inventory simulation configuration.");
        Guid? selected = null;
        if (configuration["Simulation:OrderId"] is string value)
        {
            if (!Guid.TryParse(value, out var id) || id == Guid.Empty) throw new ArgumentException("Simulation:OrderId must be a nonempty UUID.");
            selected = id;
        }
        return new(profile, delay, failures, selected);
    }

    public bool Selects(Guid orderId) => OrderId is null || OrderId == orderId;
    public bool Fails(Guid orderId, int attempt) => Selects(orderId) && Profile == "temporary-inventory-failure" && attempt <= FailuresBeforeSuccess;
}
