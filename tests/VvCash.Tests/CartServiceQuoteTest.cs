using System.Collections.Generic;
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class CartServiceQuoteTest
{
    private static CartService CartWith(decimal price, int qty, params Promotion[] promotions)
    {
        var c = new CartService(new StubPromotionProvider(promotions));
        var p = new Product { Id = "p1", Name = "X", Price = price };
        for (int i = 0; i < qty; i++) c.AddProduct(p);
        return c;
    }

    private static Promotion PercentOff(string id, string name, decimal percent) => new()
    {
        Id = id,
        Name = name,
        Enabled = true,
        AutoApply = true,
        ApplyScope = "cart",
        Rules = new List<PromotionRule>
        {
            new() { QtyOp = "min", QtyFrom = 1, Effect = "percent", Value = percent },
        },
    };

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

    [Fact]
    public void OfflinePromotion_AppliesWhenThereIsNoQuote()
    {
        var c = CartWith(100m, 1, PercentOff("promo1", "Летняя акция", 15m));

        Assert.Equal(15m, c.TotalDiscount);
        Assert.Equal(85m, c.TotalAmount);
        Assert.Equal("Летняя акция", c.AppliedDiscountName);
    }

    [Fact]
    public void OfflinePromotion_IsIgnoredWhileAQuoteIsApplied()
    {
        // The server already weighed promotions into its best-deal; applying the
        // local one on top would double-discount the cart.
        var c = CartWith(100m, 1, PercentOff("promo1", "Летняя акция", 15m));
        c.ApplyQuote(new QuoteResult { QuoteId = "q1", DiscountTotal = 5m });

        Assert.Equal(5m, c.TotalDiscount);
        Assert.Null(c.OfflinePromotion);
    }

    [Fact]
    public void OfflinePromotion_CompetesWithFlatCustomerPercent_BestWins()
    {
        var c = CartWith(100m, 1, PercentOff("promo1", "Летняя акция", 15m));
        c.SetCustomerDiscount(10m); // flat 10 < promotion 15

        Assert.Equal(15m, c.TotalDiscount);

        c.SetCustomerDiscount(25m); // flat 25 > promotion 15
        Assert.Equal(25m, c.TotalDiscount);
    }

    [Fact]
    public void AppliedDiscountName_PrefersQuoteNameOverRef()
    {
        var c = CartWith(100m, 1);
        c.ApplyQuote(new QuoteResult
        {
            QuoteId = "q1",
            DiscountTotal = 20m,
            Applied = { new QuoteApplied { Kind = "promotion", Ref = "uuid-1", Name = "3 по цене 2" } },
        });

        Assert.Equal("3 по цене 2", c.AppliedDiscountName);
    }

    [Fact]
    public void OfflinePromotion_IsNotReported_WhenTheFlatPathWins()
    {
        // The sale reports OfflinePromotion's id to the server, which charges a use
        // against max_uses. A promotion that lost to the flat customer percent
        // granted nothing and must not be billed for it.
        var c = CartWith(100m, 1, PercentOff("promo1", "Летняя акция", 15m));
        c.SetCustomerDiscount(25m);

        Assert.Equal(25m, c.TotalDiscount);
        Assert.Null(c.OfflinePromotion);
        Assert.Null(c.AppliedDiscountName);
    }

    [Fact]
    public void OfflinePromotion_IsReported_WhenItBeatsTheFlatPath()
    {
        var c = CartWith(100m, 1, PercentOff("promo1", "Летняя акция", 15m));
        c.SetCustomerDiscount(10m);

        Assert.Equal("promo1", c.OfflinePromotion?.PromotionId);
    }

    [Fact]
    public void SetQuantity_AcceptsFractionalWeight()
    {
        var c = CartWith(100m, 1);

        c.SetQuantity(c.Items[0], 1.4m);

        Assert.Equal(1.4m, c.Items[0].Quantity);
        Assert.Equal(140m, c.Subtotal);
    }

    [Fact]
    public void SetQuantity_RemovesTheLineAtZero()
    {
        var c = CartWith(100m, 1);

        c.SetQuantity(c.Items[0], 0m);

        Assert.Empty(c.Items);
    }

    [Fact]
    public void OfflinePromotion_UsesTheStoreRoundingPolicy()
    {
        // 33.33% of 100 is 33.33 half-up but 33.34 rounding away from zero on any
        // remainder — proof the policy reaches the calculator instead of a
        // hardcoded 2-place half-up.
        var provider = new StubPromotionProvider(PercentOff("promo1", "Акция", 33.333m))
        {
            MoneyPolicy = new MoneyPolicy { Scale = 2, Mode = "UP" },
        };
        var c = new CartService(provider);
        c.AddProduct(new Product { Id = "p1", Name = "X", Price = 100m });

        Assert.Equal(33.34m, c.TotalDiscount);
    }

    [Fact]
    public void AppliedDiscountName_FallsBackToRef_WhenServerSendsNoName()
    {
        var c = CartWith(100m, 1);
        c.ApplyQuote(new QuoteResult
        {
            QuoteId = "q1",
            DiscountTotal = 20m,
            Applied = { new QuoteApplied { Kind = "card", Ref = "card-42" } },
        });

        Assert.Equal("card-42", c.AppliedDiscountName);
    }

    // ── line prices come from the quote ──────────────────────────────────────────
    // The server prices the cart from the warehouse catalog and ignores the unit price
    // the register sends, so a register whose product cache has gone stale must show
    // and charge the server's price, not its own.

    private static QuoteResult QuoteWithLine(string productId, decimal unitPrice, decimal discountTotal = 0m) => new()
    {
        QuoteId = "q1",
        DiscountTotal = discountTotal,
        Lines = { new QuoteLineResult { ProductId = productId, UnitPrice = unitPrice } },
    };

    [Fact]
    public void ApplyQuote_PricesLinesAtTheServersUnitPrice()
    {
        var c = CartWith(100m, 2); // register's cached price: 100 × 2 = 200
        c.ApplyQuote(QuoteWithLine("p1", 90m));

        Assert.Equal(90m, c.Items[0].UnitPrice);
        Assert.Equal(180m, c.Items[0].LineTotal);
        Assert.Equal(180m, c.Subtotal);
    }

    [Fact]
    public void QuotedLine_FollowsAQuantityChange()
    {
        // The requote is debounced, so between a +1 tap and the new quote arriving the
        // line must still read qty × the server's unit price — not the previous line total.
        var c = CartWith(100m, 2);
        c.ApplyQuote(QuoteWithLine("p1", 90m));

        c.IncreaseQuantity(c.Items[0]);

        Assert.Equal(270m, c.Items[0].LineTotal);
    }

    [Fact]
    public void ApplyQuote_LeavesLinesTheServerDidNotPrice_OnTheirCachedPrice()
    {
        var c = CartWith(100m, 1);
        c.AddProduct(new Product { Id = "p2", Name = "Y", Price = 50m });
        c.ApplyQuote(QuoteWithLine("p1", 90m));

        Assert.Equal(90m, c.Items[0].UnitPrice);
        Assert.Equal(50m, c.Items[1].UnitPrice);
    }

    [Fact]
    public void ClearQuote_RestoresTheCachedPrice()
    {
        var c = CartWith(100m, 2);
        c.ApplyQuote(QuoteWithLine("p1", 90m));
        c.ClearQuote();

        Assert.Equal(100m, c.Items[0].UnitPrice);
        Assert.Equal(200m, c.Subtotal);
    }

    private static QuoteResult QuoteWithDiscountedLine(
        string productId, decimal quantity, decimal unitPrice,
        decimal discountAmount, decimal discountPercent) => new()
    {
        QuoteId = "q1",
        DiscountTotal = discountAmount,
        Lines =
        {
            new QuoteLineResult
            {
                ProductId = productId,
                Quantity = quantity,
                UnitPrice = unitPrice,
                DiscountAmount = discountAmount,
                DiscountPercent = discountPercent,
            },
        },
    };

    /// <summary>Per unit, not per line. The quote reports discount_amount for the whole
    /// line; CartItem.LineDiscount multiplies back up by quantity, so storing the line
    /// figure here would triple a three-unit discount on screen.</summary>
    [Fact]
    public void ApplyQuote_SplitsTheLineDiscountAcrossUnits()
    {
        var c = CartWith(100m, 3);

        c.ApplyQuote(QuoteWithDiscountedLine("p1", quantity: 3m, unitPrice: 100m,
                                             discountAmount: 30m, discountPercent: 10m));

        Assert.Equal(10m, c.Items[0].QuotedUnitDiscount);
        Assert.Equal(10m, c.Items[0].QuotedDiscountPercent);
        Assert.Equal(30m, c.Items[0].LineDiscount);
        Assert.True(c.Items[0].HasLineDiscount);
    }

    /// <summary>The same input has to produce the same number the exchange screen
    /// produces, because both render it as "what came off this line".</summary>
    [Fact]
    public void ApplyQuote_MatchesTheExchangeScreensArithmetic()
    {
        // Exactly what ExchangeViewModel.ApplyIssuedQuote computes for this line.
        var expected = 7m / 4m;

        var c = CartWith(25m, 4);
        c.ApplyQuote(QuoteWithDiscountedLine("p1", quantity: 4m, unitPrice: 25m,
                                             discountAmount: 7m, discountPercent: 7m));

        Assert.Equal(expected, c.Items[0].QuotedUnitDiscount);
    }

    /// <summary>Dropping the quote drops the cart back to cached prices. If the discount
    /// fields survive that, the badge outlives the promotion that justified it.</summary>
    [Fact]
    public void ClearQuote_ClearsTheDiscountFields()
    {
        var c = CartWith(100m, 2);
        c.ApplyQuote(QuoteWithDiscountedLine("p1", quantity: 2m, unitPrice: 90m,
                                             discountAmount: 20m, discountPercent: 10m));
        Assert.True(c.Items[0].HasLineDiscount);

        c.ClearQuote();

        Assert.Null(c.Items[0].QuotedUnitDiscount);
        Assert.Equal(0m, c.Items[0].QuotedDiscountPercent);
        Assert.False(c.Items[0].HasLineDiscount);
    }
}
