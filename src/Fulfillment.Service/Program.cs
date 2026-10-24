using Fulfillment.Service;
using Playground.Infrastructure;

var builder = FoundationHost.Create(args, "Fulfillment.Service");
builder.Services.AddSingleton<FulfillmentStore>();
builder.Services.AddSingleton(provider =>
{
    var settings = provider.GetRequiredService<LabSettings>();
    var url = builder.Configuration["Services:CouriersUrl"] ?? $"http://127.0.0.1:{settings.RabbitPort + 15}";
    return new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(5) };
});
builder.Services.AddSingleton<FulfillmentProcessor>();
builder.Services.AddSingleton<FulfillmentConsumer>();
builder.Services.AddHostedService<FulfillmentWorker>();
builder.Services.AddSingleton<WarehouseRequestWriter>();
builder.Services.AddHostedService<WarehouseRequestWorker>();
var app = builder.Build();
app.Services.GetRequiredService<FulfillmentStore>().Initialize();
app.MapFoundation("Fulfillment.Service");
app.MapFulfillment();
await app.RunAsync();