namespace Couriers.Api;

public sealed record CourierSimulation(string Profile, int DelayMs, int FailuresBeforeSuccess, Guid? OrderId)
{
    public bool Selects(Guid orderId) => OrderId is null || OrderId == orderId;
    public static CourierSimulation Read(IConfiguration config)
    {
        var profile = config["Simulation:Profile"] ?? "happy-path";
        // Other hosts receive the same environment when started by lab.ps1.
        var delay = config.GetValue("Simulation:DelayMs", profile == "slow-courier" ? 6000 : 0);
        var failures = config.GetValue("Simulation:FailuresBeforeSuccess", 0);
        var order = config.GetValue<Guid?>("Simulation:OrderId");
        if (delay < 0 || failures < 0 || order == Guid.Empty) throw new ArgumentException("Invalid courier simulation settings.");
        return new(profile, delay, failures, order);
    }
}