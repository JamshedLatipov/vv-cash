using System.Collections.Generic;
using System.Linq;
using VvCash.Models;
using VvCash.Models.Api;

namespace VvCash.Services;

public static class QuoteRequestBuilder
{
    /// <summary>Builds a quote request for the current cart. <paramref name="warehouseId"/>
    /// is optional: when the register does not know it, the field is omitted and the server
    /// resolves the warehouse from the cash token.</summary>
    public static QuoteRequest Build(IEnumerable<CartItem> items, string? warehouseId, string? cardIdentifier, string? code)
    {
        return new QuoteRequest
        {
            WarehouseId = string.IsNullOrWhiteSpace(warehouseId) ? null : warehouseId.Trim(),
            CardIdentifier = string.IsNullOrWhiteSpace(cardIdentifier) ? null : cardIdentifier.Trim(),
            Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
            Lines = items.Select(i => new QuoteLineInput
            {
                ProductId = i.Product.Id,
                Quantity = i.Quantity,
                UnitPrice = i.Product.Price
            }).ToList()
        };
    }
}
