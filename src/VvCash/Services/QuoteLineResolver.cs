using System;
using System.Linq;
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services.Discounts;

namespace VvCash.Services;

public static class QuoteLineResolver
{
    /// <summary>Returns (discountPercent, priceBeforeDiscount) for a receipt line.
    /// Priority: the server quote, then the locally computed promotion, then the
    /// product's own flat fields.</summary>
    /// <param name="lineIndex">Cart position of <paramref name="item"/>. The offline
    /// promotion addresses lines by index, not by product id, because the same
    /// product can legitimately appear on more than one line.</param>
    public static (decimal discountPercent, decimal priceBeforeDiscount) Resolve(
        QuoteResult? quote, PromotionOutcome? offlinePromotion, CartItem item, int lineIndex,
        MoneyPolicy? policy = null)
    {
        policy ??= MoneyPolicy.Default;

        if (quote != null)
        {
            var line = quote.Lines.FirstOrDefault(l => l.ProductId == item.Product.Id);
            if (line != null)
                return (line.DiscountPercent, line.UnitPrice);
        }

        if (offlinePromotion != null
            && offlinePromotion.PerLine.TryGetValue(lineIndex, out var amount)
            && amount > 0)
        {
            var subtotal = item.Product.Price * item.Quantity;
            if (subtotal > 0)
            {
                var percent = policy.Round(amount / subtotal * 100m);
                return (percent, item.Product.Price);
            }
        }

        return (item.Product.DiscountPercent ?? 0m, item.Product.OriginalPrice ?? item.Product.Price);
    }
}
