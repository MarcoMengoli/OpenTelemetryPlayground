namespace Payments.Api;

public sealed record PaymentSimulation(string Profile, int DelayMs, int FailuresBeforeSuccess, Guid? OrderId)
{
    public static PaymentSimulation Read(IConfiguration configuration)
    {
        var profile = configuration["Simulation:Profile"] ?? "happy-path";
        if (profile is not ("happy-path" or "payment-declined" or "temporary-payment-failure"))
            throw new ArgumentException("Unknown Payments simulation profile.");
        var delay = configuration.GetValue<int>("Simulation:DelayMs");
        var failures = configuration.GetValue<int>("Simulation:FailuresBeforeSuccess");
        if (delay is < 0 or > 60000 || failures is < 0 or > 1000)
            throw new ArgumentException("Invalid Payments simulation delay or failure count.");
        var order = configuration["Simulation:OrderId"];
        Guid? selected = null;
        if (order is not null)
        {
            if (!Guid.TryParse(order, out var id) || id == Guid.Empty)
                throw new ArgumentException("Simulation:OrderId must be a nonempty UUID.");
            selected = id;
        }
        return new(profile, delay, failures, selected);
    }

    public bool Selects(Guid orderId) => OrderId is null || OrderId == orderId;
    public bool Declines(Guid orderId) => Selects(orderId) && Profile == "payment-declined";
    public bool Fails(Guid orderId, long attempt) => Selects(orderId) && Profile == "temporary-payment-failure" && attempt <= FailuresBeforeSuccess;
}
