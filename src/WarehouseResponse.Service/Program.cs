using Playground.Infrastructure;
using Playground.WarehouseResponses;

var builder = FoundationHost.Create(args, "WarehouseResponse.Service");
builder.Services.AddSingleton<ResponseStore>();
builder.Services.AddSingleton(provider => new DurableOutbox(provider.GetRequiredService<ResponseStore>().Database));
builder.Services.AddSingleton(provider =>
{
    var settings = provider.GetRequiredService<LabSettings>();
    var url = builder.Configuration["Services:FulfillmentBaseUrl"] ?? $"http://127.0.0.1:{settings.RabbitPort + 14}";
    return new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(5) };
});
builder.Services.AddSingleton<ResponseReader>();
builder.Services.AddSingleton<ResponseLookup>();
builder.Services.AddHostedService<ResponseWorker>();
builder.Services.AddHostedService<OutboxPump>();
var app = builder.Build();
app.Services.GetRequiredService<ResponseStore>().Initialize();
app.MapFoundation("WarehouseResponse.Service");
await app.RunAsync();
