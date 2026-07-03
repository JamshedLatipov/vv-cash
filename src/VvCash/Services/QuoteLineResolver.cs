using System.Linq;
using VvCash.Models;
using VvCash.Models.Api;

namespace VvCash.Services;

public static class QuoteLineResolver
{
    /// <summary>Returns (discountPercent, priceBeforeDiscount) for a receipt line.
    /// Server quote takes priority; otherwise the product's flat fields.</summary>
    public static (decimal discountPercent, decimal priceBeforeDiscount) Resolve(QuoteResult? quote, CartItem item)
    {
        if (quote != null)
        {
            var line = quote.Lines.FirstOrDefault(l => l.ProductId == item.Product.Id);
            if (line != null)
                return (line.DiscountPercent, line.UnitPrice);
        }
        return (item.Product.DiscountPercent ?? 0m, item.Product.OriginalPrice ?? item.Product.Price);
    }
}
