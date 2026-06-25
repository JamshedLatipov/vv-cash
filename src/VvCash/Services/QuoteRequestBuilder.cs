using System.Collections.Generic;
using System.Linq;
using VvCash.Models;
using VvCash.Models.Api;

namespace VvCash.Services;

public static class QuoteRequestBuilder
{
    public static QuoteRequest Build(IEnumerable<CartItem> items, string warehouseId, string? cardIdentifier, string? code)
    {
        return new QuoteRequest
        {
            WarehouseId = warehouseId,
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
