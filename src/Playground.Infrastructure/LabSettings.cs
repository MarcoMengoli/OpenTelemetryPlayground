using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;

namespace Playground.Infrastructure;

public sealed record LabSettings(string Name, string Root, string RabbitHost, int RabbitPort, string RabbitUser, string RabbitPassword)
{
    public string VirtualHost => Name;
    public string Exchange => $"{Name}.business.v1";
    public string ErrorExchange => $"{Name}.errors.v1";
    public string Warehouse => Path.Combine(Root, "warehouse");
    public string Database(string service) => Path.Combine(Root, "data", service, "state.db");
    public static LabSettings Read(IConfiguration config)
    {
        var name = config["Lab:Name"] ?? "local";
        if (!Regex.IsMatch(name, "^[a-z][a-z0-9-]{0,31}$")) throw new ArgumentException("Lab:Name must match ^[a-z][a-z0-9-]{0,31}$.");
        var port = int.Parse(config["Lab:RabbitPort"] ?? "18200");
        if (port is < 1 or > 65535) throw new ArgumentException("Invalid RabbitMQ port.");
        return new(name, Path.GetFullPath(config["Lab:Root"] ?? Path.Combine(".local", name)),
            config["Lab:RabbitHost"] ?? "127.0.0.1", port,
            config["Lab:RabbitUser"] ?? "playground", config["Lab:RabbitPassword"] ?? "local-playground-only");
    }
}
