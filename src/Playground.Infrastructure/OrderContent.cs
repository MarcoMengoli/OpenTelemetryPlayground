using System.Globalization;
using Playground.Contracts;

namespace Playground.Infrastructure;

// Canonical business input shared only by Checkout and Orders; no wire fields added.
public static class OrderContent
{
    public static CheckoutRequest Normalize(CheckoutRequest request)
    {
        request.Validate();
        _ = Total(request.Items);
        return request with { Items = request.Items.OrderBy(x => x.Sku, StringComparer.Ordinal)
            .Select(x => x with { UnitPrice = decimal.Parse(x.UnitPrice.ToString("G29", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture) }).ToArray() };
    }

    public static Money Total(OrderLine[] items)
    {
        decimal total = 0;
        foreach (var item in items) total = checked(total + checked(item.Quantity * item.UnitPrice));
        var money = new Money(total, "EUR");
        money.Validate();
        return money;
    }
}
