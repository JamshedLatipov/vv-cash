using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class QuoteLineResolverTest
{
    [Fact]
    public void Resolve_UsesQuoteLine_WhenPresent()
    {
        var quote = new QuoteResult
        {
            Lines = { new QuoteLineResult { ProductId = "p1", DiscountPercent = 15m, UnitPrice = 85m } }
        };
        // Product.Price differs from the quote's UnitPrice to prove the quote line wins.
        var item = new CartItem { Product = new Product { Id = "p1", Price = 100m }, Quantity = 1 };

        var (pct, before) = QuoteLineResolver.Resolve(quote, null, item, 0);

        Assert.Equal(15m, pct);
        Assert.Equal(85m, before);
    }

    [Fact]
    public void Resolve_FallsBackToProduct_WhenNoQuote()
    {
        var item = new CartItem
        {
            Product = new Product { Id = "p9", Price = 80m, OriginalPrice = 90m, DiscountPercent = 10m },
            Quantity = 1
        };

        var (pct, before) = QuoteLineResolver.Resolve(null, null, item, 0);

        Assert.Equal(10m, pct);
        Assert.Equal(90m, before);
    }

    [Fact]
    public void Resolve_FallsBackToProduct_WhenLineMissingInQuote()
    {
        var quote = new QuoteResult { Lines = { new QuoteLineResult { ProductId = "other" } } };
        var item = new CartItem { Product = new Product { Id = "p1", Price = 50m }, Quantity = 1 };

        var (pct, before) = QuoteLineResolver.Resolve(quote, null, item, 0);

        Assert.Equal(0m, pct);
        Assert.Equal(50m, before);
    }
}
