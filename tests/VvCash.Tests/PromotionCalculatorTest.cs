using System;
using System.Collections.Generic;
using VvCash.Models;
using VvCash.Services.Discounts;
using Xunit;

namespace VvCash.Tests;

/// <summary>Locks the offline calculator to the server's promotion pricing. Every
/// case here mirrors one in cloudmarket-server's discounts/source_promotion_test.go:
/// when the two disagree, a customer is charged one price online and another
/// offline for the same basket.</summary>
public class PromotionCalculatorTest
{
    // "Каждый 2-й бесплатно": 1 unit → 30% off; 2+ units → buy1get1, cheapest free.
    private static Promotion Ladder() => new()
    {
        Id = "promo1",
        Name = "Каждый 2-й бесплатно",
        Enabled = true,
        AutoApply = true,
        ApplyScope = "cart",
        Rules = new List<PromotionRule>
        {
            new() { QtyOp = "exact", QtyFrom = 1, Effect = "percent", Value = 30m },
            new() { QtyOp = "min", QtyFrom = 2, Effect = "cheapest_free", Value = 100m,
                    BuyQty = 1, GetQty = 1, Repeat = true },
        },
    };

    private static PromoCartLine Line(string id, decimal qty, decimal price, string category = "")
        => new() { ProductId = id, Quantity = qty, UnitPrice = price, CategoryId = category };

    // 2×100 (dairy) + 1×20 (bread) = 3 units, subtotal 220.
    private static List<PromoCartLine> MixedCart() => new()
    {
        Line("shirt", 2, 100m, "dairy"),
        Line("socks", 1, 20m, "bread"),
    };

    [Fact]
    public void OneUnit_TakesPercentRung()
    {
        var r = PromotionCalculator.Evaluate(Ladder(), new List<PromoCartLine> { Line("shirt", 1, 100m) });

        Assert.Equal(30m, r.Total);
        Assert.Equal("promo1", r.PromotionId);
    }

    [Fact]
    public void ThreeUnits_FreesCheapestUnit()
    {
        var r = PromotionCalculator.Evaluate(Ladder(), MixedCart());

        Assert.Equal(20m, r.Total);
        Assert.Equal(new[] { 1 }, r.PerLine.Keys);
    }

    [Fact]
    public void FourUnits_RepeatFreesTwo()
    {
        var cart = new List<PromoCartLine> { Line("shirt", 2, 100m), Line("socks", 2, 20m) };

        Assert.Equal(40m, PromotionCalculator.Evaluate(Ladder(), cart).Total);
    }

    [Fact]
    public void RepeatOff_FreesOnlyOnce()
    {
        var p = Ladder();
        p.Rules[1].Repeat = false;
        var cart = new List<PromoCartLine> { Line("shirt", 2, 100m), Line("socks", 2, 20m) };

        Assert.Equal(20m, PromotionCalculator.Evaluate(p, cart).Total);
    }

    [Fact]
    public void HalfPriceGift_DiscountsCheapestUnitByValuePercent()
    {
        var p = Ladder();
        p.Rules[1].Value = 50m;

        Assert.Equal(10m, PromotionCalculator.Evaluate(p, MixedCart()).Total);
    }

    [Fact]
    public void TargetedByCategory_CountsOnlyMatchingLines()
    {
        var p = Ladder();
        p.ApplyScope = "lines";
        p.Targets = new List<PromotionTarget> { new() { TargetType = "category", TargetId = "dairy" } };

        // Only the 2 shirts participate, so the cheapest matching unit costs 100.
        var r = PromotionCalculator.Evaluate(p, MixedCart());

        Assert.Equal(100m, r.Total);
        Assert.Equal(new[] { 0 }, r.PerLine.Keys);
    }

    [Fact]
    public void TargetedByTag_MatchesOnTagId()
    {
        var p = Ladder();
        p.ApplyScope = "lines";
        p.Targets = new List<PromotionTarget> { new() { TargetType = "tag", TargetId = "t-sale" } };

        var cart = new List<PromoCartLine>
        {
            new() { ProductId = "a", Quantity = 2, UnitPrice = 100m, TagIds = new[] { "t-sale" } },
            new() { ProductId = "b", Quantity = 1, UnitPrice = 20m, TagIds = new[] { "t-other" } },
        };

        var r = PromotionCalculator.Evaluate(p, cart);

        Assert.Equal(100m, r.Total);
        Assert.Equal(new[] { 0 }, r.PerLine.Keys);
    }

    [Fact]
    public void NoMatchingLines_GivesNothing()
    {
        var p = Ladder();
        p.ApplyScope = "lines";
        p.Targets = new List<PromotionTarget> { new() { TargetType = "product", TargetId = "absent" } };

        Assert.Equal(0m, PromotionCalculator.Evaluate(p, MixedCart()).Total);
    }

    [Fact]
    public void AmountEffect_CapsAtMatchingSubtotal()
    {
        var p = new Promotion
        {
            Id = "p2",
            ApplyScope = "cart",
            Rules = new List<PromotionRule>
            {
                new() { QtyOp = "min", QtyFrom = 1, Effect = "amount", Value = 9999m },
            },
        };

        Assert.Equal(220m, PromotionCalculator.Evaluate(p, MixedCart()).Total);
    }

    [Fact]
    public void PercentEffect_DiscountsEveryMatchingLine()
    {
        var p = new Promotion
        {
            Id = "pPercent",
            ApplyScope = "cart",
            Rules = new List<PromotionRule>
            {
                new() { QtyOp = "min", QtyFrom = 1, Effect = "percent", Value = 10m },
            },
        };
        var cart = new List<PromoCartLine> { Line("a", 2, 100m), Line("b", 3, 30m) };

        var r = PromotionCalculator.Evaluate(p, cart);

        Assert.Equal(29m, r.Total);
        Assert.Equal(20m, r.PerLine[0]); // 10% of 200
        Assert.Equal(9m, r.PerLine[1]);  // 10% of 90
    }

    [Fact]
    public void AmountEffect_AllocatesProportionallyToSubtotal()
    {
        var p = new Promotion
        {
            Id = "pAmount",
            ApplyScope = "cart",
            Rules = new List<PromotionRule>
            {
                new() { QtyOp = "min", QtyFrom = 1, Effect = "amount", Value = 60m },
            },
        };
        var cart = new List<PromoCartLine> { Line("a", 2, 100m), Line("b", 2, 50m) };

        var r = PromotionCalculator.Evaluate(p, cart);

        Assert.Equal(60m, r.Total);
        Assert.Equal(40m, r.PerLine[0]); // 60 * 200/300
        Assert.Equal(20m, r.PerLine[1]); // 60 * 100/300
    }

    [Fact]
    public void AmountEffect_PerLineSumsExactlyToTotal_EvenWithRoundingResidual()
    {
        var p = new Promotion
        {
            Id = "pAmountSum",
            ApplyScope = "cart",
            Rules = new List<PromotionRule>
            {
                new() { QtyOp = "min", QtyFrom = 1, Effect = "amount", Value = 10m },
            },
        };
        // A 3-way split of 10 is not exact at 2 decimal places.
        var cart = new List<PromoCartLine> { Line("a", 1, 100m), Line("b", 1, 100m), Line("c", 1, 100m) };

        var r = PromotionCalculator.Evaluate(p, cart);

        Assert.Equal(10m, r.Total);
        decimal sum = 0m;
        foreach (var v in r.PerLine.Values) sum += v;
        Assert.Equal(r.Total, sum);
    }

    [Fact]
    public void Buy2Get1_FreesCheapestUnits()
    {
        var p = new Promotion
        {
            Id = "pBxgy",
            ApplyScope = "cart",
            Rules = new List<PromotionRule>
            {
                new() { QtyOp = "min", QtyFrom = 1, Effect = "cheapest_free", Value = 100m,
                        BuyQty = 2, GetQty = 1, Repeat = true },
            },
        };
        // 6 units, group size 3 → 2 groups → 2 free units, cheapest = socks@20.
        var cart = new List<PromoCartLine> { Line("shirt", 2, 100m), Line("socks", 4, 20m) };

        var r = PromotionCalculator.Evaluate(p, cart);

        Assert.Equal(40m, r.Total);
        Assert.Equal(new[] { 1 }, r.PerLine.Keys);
    }

    [Fact]
    public void RepeatOff_FreesExactlyGetQtyNotBuyQty()
    {
        var p = new Promotion
        {
            Id = "pRepeatOff",
            ApplyScope = "cart",
            Rules = new List<PromotionRule>
            {
                new() { QtyOp = "min", QtyFrom = 1, Effect = "cheapest_free", Value = 100m,
                        BuyQty = 3, GetQty = 2, Repeat = false },
            },
        };
        // 5 units → 1 group of (3+2); repeat off frees GetQty=2 cheapest (2×20),
        // never BuyQty=3, which would spill into the 100 line.
        var cart = new List<PromoCartLine> { Line("shirt", 3, 100m), Line("socks", 2, 20m) };

        Assert.Equal(40m, PromotionCalculator.Evaluate(p, cart).Total);
    }

    [Fact]
    public void FractionalQuantity_TruncatesToWholeUnits()
    {
        // qty 2.5 clears the "from 2" rung; 2 whole units → 1 group → 1 free at 10.
        var cart = new List<PromoCartLine> { Line("weight", 2.5m, 10m) };

        Assert.Equal(10m, PromotionCalculator.Evaluate(Ladder(), cart).Total);
    }

    [Fact]
    public void FractionalWeightLines_ClearRungButFreeNothing()
    {
        // 0.5 + 0.6 + 0.9 = 2.0 clears the rung, but every line truncates to 0 units.
        var cart = new List<PromoCartLine>
        {
            Line("cheese-a", 0.5m, 100m),
            Line("cheese-b", 0.6m, 100m),
            Line("cheese-c", 0.9m, 100m),
        };

        Assert.Equal(0m, PromotionCalculator.Evaluate(Ladder(), cart).Total);
    }

    [Fact]
    public void CheapestFree_WithMissingOrZeroQty_GivesNothing()
    {
        var rules = new[]
        {
            new PromotionRule { QtyOp = "min", QtyFrom = 1, Effect = "cheapest_free", Value = 100m },
            new PromotionRule { QtyOp = "min", QtyFrom = 1, Effect = "cheapest_free", Value = 100m,
                                BuyQty = 0, GetQty = 0 },
        };

        foreach (var rule in rules)
        {
            var p = new Promotion
            {
                Id = "pNilZero",
                ApplyScope = "cart",
                Rules = new List<PromotionRule> { rule },
            };
            var r = PromotionCalculator.Evaluate(p, MixedCart());

            Assert.Equal(0m, r.Total);
            Assert.Empty(r.PerLine);
        }
    }

    [Fact]
    public void Rungs_DoNotStack()
    {
        var p = new Promotion
        {
            Id = "pStack",
            ApplyScope = "cart",
            Rules = new List<PromotionRule>
            {
                new() { QtyOp = "exact", QtyFrom = 3, Effect = "percent", Value = 20m },
                new() { QtyOp = "min", QtyFrom = 3, Effect = "amount", Value = 999m },
            },
        };
        // qty 3 satisfies both rungs; only the first may apply.
        var cart = new List<PromoCartLine> { Line("a", 3, 100m) };

        Assert.Equal(60m, PromotionCalculator.Evaluate(p, cart).Total);
    }

    [Fact]
    public void BestDeal_PicksTheLargestDiscount()
    {
        var small = new Promotion
        {
            Id = "small", Name = "Small", Enabled = true, AutoApply = true, ApplyScope = "cart",
            Rules = new List<PromotionRule>
            {
                new() { QtyOp = "min", QtyFrom = 1, Effect = "percent", Value = 5m },
            },
        };
        var big = new Promotion
        {
            Id = "big", Name = "Big", Enabled = true, AutoApply = true, ApplyScope = "cart",
            Rules = new List<PromotionRule>
            {
                new() { QtyOp = "min", QtyFrom = 1, Effect = "percent", Value = 25m },
            },
        };
        var cart = new List<PromoCartLine> { Line("a", 1, 100m) };

        var winner = PromotionCalculator.BestDeal(new[] { small, big }, cart, DateTimeOffset.Now);

        Assert.NotNull(winner);
        Assert.Equal("big", winner!.PromotionId);
        Assert.Equal(25m, winner.Total);
    }

    [Fact]
    public void BestDeal_SkipsIneligiblePromotions()
    {
        var now = new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
        var rules = new List<PromotionRule>
        {
            new() { QtyOp = "min", QtyFrom = 1, Effect = "percent", Value = 50m },
        };
        var cart = new List<PromoCartLine> { Line("a", 1, 100m) };

        var notStarted = new Promotion
        {
            Id = "future", Enabled = true, AutoApply = true, ApplyScope = "cart",
            StartsAt = now.AddDays(1), Rules = rules,
        };
        var expired = new Promotion
        {
            Id = "past", Enabled = true, AutoApply = true, ApplyScope = "cart",
            EndsAt = now.AddDays(-1), Rules = rules,
        };
        var exhausted = new Promotion
        {
            Id = "used-up", Enabled = true, AutoApply = true, ApplyScope = "cart",
            MaxUses = 2, UsedCount = 2, Rules = rules,
        };
        var disabled = new Promotion
        {
            Id = "off", Enabled = false, AutoApply = true, ApplyScope = "cart", Rules = rules,
        };
        var manual = new Promotion
        {
            Id = "manual", Enabled = true, AutoApply = false, ApplyScope = "cart", Rules = rules,
        };

        Assert.Null(PromotionCalculator.BestDeal(
            new[] { notStarted, expired, exhausted, disabled, manual }, cart, now));
    }
}
