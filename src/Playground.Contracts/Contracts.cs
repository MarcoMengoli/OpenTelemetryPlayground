using System.Text.Json;
using System.Text.Json.Serialization;

namespace Playground.Contracts;

public interface IContract { void Validate(); }

public static class WireJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter<OrderStatus>(allowIntegerValues: false),
                       new JsonStringEnumConverter<PaymentDecision>(allowIntegerValues: false),
                       new JsonStringEnumConverter<PaymentStatus>(allowIntegerValues: false) }
    };
    public static T Read<T>(string json) where T : IContract
    {
        var value = JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Null payload.");
        value.Validate();
        return value;
    }
    public static string Write<T>(T value) where T : IContract
    {
        value.Validate();
        return JsonSerializer.Serialize(value, Options);
    }
}

public static class Check
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
    public static void Id(Guid id) => That(id != Guid.Empty, "Business identifiers must not be empty.");
    public static void Text(string? value) => That(!string.IsNullOrWhiteSpace(value), "Text must not be blank.");
    public static void Version(int version) => That(version == 1, "Only schemaVersion 1 is supported.");
    public static void Items(OrderLine[]? items)
    {
        That(items is { Length: > 0 }, "At least one item is required.");
        foreach (var item in items!) { That(item is not null, "Null item."); item!.Validate(); }
        That(items.Select(i => i.Sku).Distinct(StringComparer.Ordinal).Count() == items.Length, "Duplicate SKU.");
    }
    public static void Message(int version, Guid messageId, Guid orderId, DateTimeOffset occurredAt)
    {
        Version(version); Id(messageId); Id(orderId); That(occurredAt != default, "occurredAt is required.");
    }
}

public enum OrderStatus { Created, PaymentAuthorized, FulfillmentPending, Shipped, Captured, Completed, PaymentDeclined }
public enum PaymentDecision { Authorized, Declined }
public enum PaymentStatus { Authorized, Declined, Cancelled, Captured, Invoiced }

public sealed record Money(decimal Amount, string Currency) : IContract
{
    public void Validate() { Check.That(Amount > 0 && decimal.Round(Amount, 2) == Amount, "Positive amount with at most two decimals required."); Check.That(Currency == "EUR", "The initial playground uses EUR."); }
}
public sealed record OrderLine(string Sku, int Quantity, decimal UnitPrice) : IContract
{
    public void Validate() { Check.Text(Sku); Check.That(Quantity > 0, "Positive quantity required."); new Money(UnitPrice, "EUR").Validate(); }
}
public sealed record ShippingAddress(string Recipient, string Street, string City, string PostalCode, string CountryCode) : IContract
{
    public void Validate() { Check.Text(Recipient); Check.Text(Street); Check.Text(City); Check.Text(PostalCode); Check.That(CountryCode is { Length: 2 } && CountryCode.All(c => c is >= 'A' and <= 'Z'), "Uppercase ISO country code required."); }
}
public sealed record CheckoutRequest(Guid OrderId, OrderLine[] Items, ShippingAddress ShippingAddress) : IContract
{
    public void Validate() { Check.Id(OrderId); Check.Items(Items); ArgumentNullException.ThrowIfNull(ShippingAddress); ShippingAddress.Validate(); }
}
public sealed record OrderAccepted(Guid OrderId, OrderStatus Status) : IContract
{
    public void Validate() { Check.Id(OrderId); Check.That(Status == OrderStatus.Created, "New order must be Created."); }
}
public sealed record OrderSnapshot(Guid OrderId, OrderStatus Status, string? FailureCode, string? LastError, Guid? AuthorizationId, Guid? ShipmentId, Guid? CaptureId, Guid? InvoiceId) : IContract
{
    public void Validate()
    {
        Check.Id(OrderId); Check.That(Enum.IsDefined(Status), "Unknown order status.");
        foreach (var id in new[] { AuthorizationId, ShipmentId, CaptureId, InvoiceId }) if (id.HasValue) Check.Id(id.Value);
        if (Status == OrderStatus.PaymentDeclined) Check.Text(FailureCode);
        else Check.That(FailureCode is null, "Only business declines have failureCode.");
        if (Status is OrderStatus.PaymentAuthorized or OrderStatus.FulfillmentPending or OrderStatus.Shipped or OrderStatus.Captured or OrderStatus.Completed) Check.That(AuthorizationId.HasValue, "Authorization is required.");
        if (Status is OrderStatus.Shipped or OrderStatus.Captured or OrderStatus.Completed) Check.That(ShipmentId.HasValue, "Shipment is required.");
        if (Status is OrderStatus.Captured or OrderStatus.Completed) Check.That(CaptureId.HasValue, "Capture is required.");
        if (Status == OrderStatus.Completed) { Check.That(InvoiceId.HasValue, "Invoice is required."); Check.That(LastError is null, "Completed order cannot have a pending error."); }
    }
}
public sealed record AuthorizePaymentRequest(Guid OrderId, Money Total) : IContract
{
    public void Validate() { Check.Id(OrderId); ArgumentNullException.ThrowIfNull(Total); Total.Validate(); }
}
public sealed record AuthorizationResponse(Guid OrderId, Guid? AuthorizationId, PaymentDecision Decision, string? DeclineCode) : IContract
{
    public void Validate()
    {
        Check.Id(OrderId); Check.That(Enum.IsDefined(Decision), "Unknown payment decision.");
        if (Decision == PaymentDecision.Authorized) { Check.That(AuthorizationId.HasValue && DeclineCode is null, "Authorization requires an ID and no decline."); Check.Id(AuthorizationId!.Value); }
        else { Check.That(AuthorizationId is null, "Declined authorization has no ID."); Check.Text(DeclineCode); }
    }
}
public sealed record CapturePaymentRequest(Guid OrderId, Guid AuthorizationId) : IContract
{
    public void Validate() { Check.Id(OrderId); Check.Id(AuthorizationId); }
}
public sealed record CapturePaymentResponse(Guid OrderId, Guid AuthorizationId, Guid CaptureId, Money Total) : IContract
{
    public void Validate() { Check.Id(OrderId); Check.Id(AuthorizationId); Check.Id(CaptureId); ArgumentNullException.ThrowIfNull(Total); Total.Validate(); }
}
public sealed record DeliveryEstimateRequest(Guid OrderId, ShippingAddress ShippingAddress) : IContract
{
    public void Validate() { Check.Id(OrderId); ArgumentNullException.ThrowIfNull(ShippingAddress); ShippingAddress.Validate(); }
}
public sealed record DeliveryEstimateResponse(Guid OrderId, string CourierCode, int EstimatedDays) : IContract
{
    public void Validate() { Check.Id(OrderId); Check.Text(CourierCode); Check.That(EstimatedDays > 0, "Positive delivery estimate required."); }
}
public sealed record WarehouseMappingResponse(string WarehouseReference, Guid OrderId) : IContract
{
    public void Validate() { Check.Text(WarehouseReference); Check.Id(OrderId); }
}
public sealed record ApiError(string Code, string Message) : IContract
{
    public void Validate() { Check.Text(Code); Check.Text(Message); }
}

public sealed record OrderCreated(int SchemaVersion, Guid MessageId, DateTimeOffset OccurredAt, Guid OrderId, OrderLine[] Items, ShippingAddress ShippingAddress) : IContract
{
    public void Validate() { Check.Message(SchemaVersion, MessageId, OrderId, OccurredAt); new CheckoutRequest(OrderId, Items, ShippingAddress).Validate(); }
}
public sealed record ReserveInventory(int SchemaVersion, Guid MessageId, DateTimeOffset OccurredAt, Guid OrderId, OrderLine[] Items) : IContract
{
    public void Validate() { Check.Message(SchemaVersion, MessageId, OrderId, OccurredAt); Check.Items(Items); }
}
public sealed record FulfillOrder(int SchemaVersion, Guid MessageId, DateTimeOffset OccurredAt, Guid OrderId, OrderLine[] Items, ShippingAddress ShippingAddress) : IContract
{
    public void Validate() { Check.Message(SchemaVersion, MessageId, OrderId, OccurredAt); new CheckoutRequest(OrderId, Items, ShippingAddress).Validate(); }
}
public sealed record ShipmentConfirmation(int SchemaVersion, Guid MessageId, DateTimeOffset OccurredAt, Guid OrderId, string WarehouseReference, Guid ShipmentId, DateTimeOffset ShippedAt) : IContract
{
    public void Validate() { Check.Message(SchemaVersion, MessageId, OrderId, OccurredAt); Check.Text(WarehouseReference); Check.Id(ShipmentId); Check.That(ShippedAt != default, "shippedAt is required."); }
}
public sealed record WarehouseRequest(int SchemaVersion, string WarehouseReference, OrderLine[] Items, ShippingAddress ShippingAddress, string CourierCode, int EstimatedDays) : IContract
{
    public void Validate() { Check.Version(SchemaVersion); Check.Text(WarehouseReference); Check.Items(Items); ArgumentNullException.ThrowIfNull(ShippingAddress); ShippingAddress.Validate(); Check.Text(CourierCode); Check.That(EstimatedDays > 0, "Positive delivery estimate required."); }
}
public sealed record WarehouseResponse(int SchemaVersion, string WarehouseReference, Guid ShipmentId, DateTimeOffset ShippedAt) : IContract
{
    public void Validate() { Check.Version(SchemaVersion); Check.Text(WarehouseReference); Check.Id(ShipmentId); Check.That(ShippedAt != default, "shippedAt is required."); }
}


public sealed record CancelAuthorizationRequest(Guid OrderId, Guid AuthorizationId) : IContract
{
    public void Validate() { Check.Id(OrderId); Check.Id(AuthorizationId); }
}
public sealed record CancelAuthorizationResponse(Guid OrderId, Guid AuthorizationId, PaymentStatus Status) : IContract
{
    public void Validate() { Check.Id(OrderId); Check.Id(AuthorizationId); Check.That(Status == PaymentStatus.Cancelled, "Cancellation result must be Cancelled."); }
}
public sealed record IssueInvoiceRequest(Guid OrderId, Guid CaptureId) : IContract
{
    public void Validate() { Check.Id(OrderId); Check.Id(CaptureId); }
}
public sealed record InvoiceResponse(Guid OrderId, Guid CaptureId, Guid InvoiceId, Money Total) : IContract
{
    public void Validate() { Check.Id(OrderId); Check.Id(CaptureId); Check.Id(InvoiceId); ArgumentNullException.ThrowIfNull(Total); Total.Validate(); }
}
public sealed record PaymentSnapshot(Guid OrderId, PaymentStatus Status, string? DeclineCode, Guid? AuthorizationId, Guid? CaptureId, Guid? InvoiceId, Money Total) : IContract
{
    public void Validate()
    {
        Check.Id(OrderId); Check.That(Enum.IsDefined(Status), "Unknown payment status.");
        ArgumentNullException.ThrowIfNull(Total); Total.Validate();
        if (Status == PaymentStatus.Declined) { Check.Text(DeclineCode); Check.That(AuthorizationId is null && CaptureId is null && InvoiceId is null, "Declined payment has no effects."); return; }
        Check.That(DeclineCode is null && AuthorizationId.HasValue, "Authorization is required."); Check.Id(AuthorizationId!.Value);
        if (Status is PaymentStatus.Captured or PaymentStatus.Invoiced) { Check.That(CaptureId.HasValue, "Capture is required."); Check.Id(CaptureId!.Value); }
        else Check.That(CaptureId is null, "Payment is not captured.");
        if (Status == PaymentStatus.Invoiced) { Check.That(InvoiceId.HasValue, "Invoice is required."); Check.Id(InvoiceId!.Value); }
        else Check.That(InvoiceId is null, "Payment is not invoiced.");
    }
}
