using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class CartServiceQuoteTest
{
    private static CartService CartWith(decimal price, int qty)
    {
        var c = new CartService();
        var p = new Product { Id = "p1", Name = "X", Price = price };
        for (int i = 0; i < qty; i++) c.AddProduct(p);
        return c;
    }

    [Fact]
    public void TotalDiscount_UsesQuoteDiscountTotal_WhenApplied()
    {
        var c = CartWith(100m, 1); // subtotal 100
        c.ApplyQuote(new QuoteResult { QuoteId = "q1", DiscountTotal = 20m });

        Assert.Equal("q1", c.QuoteId);
        Assert.Equal(20m, c.TotalDiscount);
        Assert.Equal(80m, c.TotalAmount);
    }

    [Fact]
    public void TotalDiscount_StacksManualOnTopOfQuote()
    {
        var c = CartWith(100m, 1);
        c.ApplyQuote(new QuoteResult { QuoteId = "q1", DiscountTotal = 20m });
        c.SetManualDiscount(0m, 5m); // +5 on top

        Assert.Equal(25m, c.TotalDiscount);
    }

    [Fact]
    public void TotalDiscount_StacksManualPercentOnTopOfQuote()
    {
        var c = CartWith(100m, 1);
        c.ApplyQuote(new QuoteResult { QuoteId = "q1", DiscountTotal = 20m });
        c.SetManualDiscount(5m, 0m); // +5% of 100 = 5 on top

        Assert.Equal(25m, c.TotalDiscount);
    }

    [Fact]
    public void TotalDiscount_ClampedToSubtotal()
    {
        var c = CartWith(100m, 1);
        c.ApplyQuote(new QuoteResult { QuoteId = "q1", DiscountTotal = 90m });
        c.SetManualDiscount(0m, 50m); // 90+50 > 100

        Assert.Equal(100m, c.TotalDiscount);
        Assert.Equal(0m, c.TotalAmount);
    }

    [Fact]
    public void ClearQuote_FallsBackToFlatCustomerPercent()
    {
        var c = CartWith(100m, 1);
        c.ApplyQuote(new QuoteResult { QuoteId = "q1", DiscountTotal = 20m });
        c.ClearQuote();
        c.SetCustomerDiscount(10m); // flat 10%

        Assert.Null(c.QuoteId);
        Assert.Equal(10m, c.TotalDiscount);
    }

    [Fact]
    public void ClearCart_ClearsQuote()
    {
        var c = CartWith(100m, 1);
        c.ApplyQuote(new QuoteResult { QuoteId = "q1", DiscountTotal = 20m });
        c.ClearCart();

        Assert.Null(c.Quote);
        Assert.Null(c.QuoteId);
    }
}
