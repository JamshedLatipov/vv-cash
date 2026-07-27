using System;
using System.Collections.Generic;
using System.Linq;
using VvCash.Models;

namespace VvCash.Services.Discounts;

/// <summary>A cart line reduced to what promotion matching needs.</summary>
public sealed class PromoCartLine
{
    public string ProductId { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public string CategoryId { get; init; } = string.Empty;
    public IReadOnlyList<string> TagIds { get; init; } = Array.Empty<string>();

    public decimal Subtotal => UnitPrice * Quantity;
}

/// <summary>What one promotion would discount: the per-line vector plus the
/// cart-wide total that best-deal compares.</summary>
public sealed class PromotionOutcome
{
    public string PromotionId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public decimal Total { get; init; }

    /// <summary>Discount per cart index. Only positive amounts are present.</summary>
    public IReadOnlyDictionary<int, decimal> PerLine { get; init; } = new Dictionary<int, decimal>();
}

/// <summary>
/// Offline port of the server's promotion pricing (<c>discounts/source_promotion.go</c>).
/// The server stays the source of truth: this runs only when the register cannot
/// reach it, so the two must agree line for line — including how money is rounded,
/// which is why every entry point takes the store's <see cref="MoneyPolicy"/>
/// rather than assuming the 2-place half-up default.
/// </summary>
public static class PromotionCalculator
{
    /// <summary>Upper bound on the whole units one line contributes, mirroring the
    /// server's guard against absurd quantities overflowing the group arithmetic.</summary>
    private const int MaxLineUnits = 1_000_000_000;

    private static decimal Round(decimal v, MoneyPolicy policy) => policy.Round(v);

    /// <summary>Whole-unit count for buy-X-get-Y, truncated toward zero. A 2.9 kg
    /// line contributes 2 units, a 0.5 kg line contributes none — matching the
    /// server, where a rung can require "qty >= 2" off summed fractional weights
    /// while no single unit is ever free.</summary>
    private static int WholeUnits(decimal q)
    {
        if (q <= 0) return 0;
        if (q > MaxLineUnits) return MaxLineUnits;
        return (int)decimal.Truncate(q);
    }

    /// <summary>Whole-cart best deal: the single promotion discounting the most.
    /// Null when nothing is eligible or every candidate discounts nothing.</summary>
    public static PromotionOutcome? BestDeal(
        IEnumerable<Promotion> promotions,
        IReadOnlyList<PromoCartLine> cart,
        DateTimeOffset now,
        MoneyPolicy? policy = null)
    {
        policy ??= MoneyPolicy.Default;
        if (promotions == null || cart == null || cart.Count == 0) return null;

        PromotionOutcome? best = null;
        foreach (var p in promotions)
        {
            if (!IsEligible(p, now)) continue;
            var outcome = Evaluate(p, cart, policy);
            if (outcome.Total <= 0) continue;
            if (best == null || outcome.Total > best.Total) best = outcome;
        }
        return best;
    }

    /// <summary>Flags, validity window and usage cap. Cart-dependent conditions
    /// live in the rules, not here.</summary>
    public static bool IsEligible(Promotion p, DateTimeOffset now)
    {
        if (p == null || !p.Enabled || !p.AutoApply) return false;
        if (p.StartsAt.HasValue && now < p.StartsAt.Value) return false;
        if (p.EndsAt.HasValue && now > p.EndsAt.Value) return false;
        if (p.MaxUses > 0 && p.UsedCount >= p.MaxUses) return false;
        return true;
    }

    /// <summary>The discount one eligible promotion produces for the cart.
    /// Eligibility is the caller's job.</summary>
    public static PromotionOutcome Evaluate(Promotion p, IReadOnlyList<PromoCartLine> cart, MoneyPolicy? policy = null)
    {
        policy ??= MoneyPolicy.Default;
        var empty = new PromotionOutcome { PromotionId = p.Id, Name = p.Name };

        var idxs = new List<int>();
        decimal qty = 0m;
        for (int i = 0; i < cart.Count; i++)
        {
            if (!MatchesLine(p, cart[i])) continue;
            idxs.Add(i);
            qty += cart[i].Quantity;
        }
        if (idxs.Count == 0) return empty;

        var rule = PickRule(p, qty);
        if (rule == null) return empty;

        return rule.Effect switch
        {
            "percent" => Percent(p, rule, cart, idxs, policy),
            "amount" => Amount(p, rule, cart, idxs, policy),
            "cheapest_free" => CheapestFree(p, rule, cart, idxs, policy),
            _ => empty,
        };
    }

    /// <summary>"cart" scope matches every line; otherwise the line must be in the
    /// target set by product, category or tag.</summary>
    public static bool MatchesLine(Promotion p, PromoCartLine line)
    {
        if (p.ApplyScope == "cart") return true;
        foreach (var t in p.Targets)
        {
            switch (t.TargetType)
            {
                case "product":
                    if (t.TargetId == line.ProductId) return true;
                    break;
                case "category":
                    if (t.TargetId == line.CategoryId) return true;
                    break;
                case "tag":
                    if (line.TagIds.Contains(t.TargetId)) return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>The first rung the quantity reaches. Rules arrive in ladder order
    /// from the backend, so the first match is the lowest position.</summary>
    public static PromotionRule? PickRule(Promotion p, decimal qty)
    {
        foreach (var r in p.Rules)
        {
            var matches = r.QtyOp switch
            {
                "exact" => qty == r.QtyFrom,
                "min" => qty >= r.QtyFrom,
                _ => false,
            };
            if (matches) return r;
        }
        return null;
    }

    private static PromotionOutcome Percent(
        Promotion p, PromotionRule rule, IReadOnlyList<PromoCartLine> cart, List<int> idxs, MoneyPolicy policy)
    {
        var perLine = new Dictionary<int, decimal>();
        decimal total = 0m;
        foreach (var i in idxs)
        {
            var a = Round(cart[i].Subtotal * rule.Value / 100m, policy);
            if (a <= 0) continue;
            perLine[i] = a;
            total += a;
        }
        return new PromotionOutcome
        {
            PromotionId = p.Id,
            Name = p.Name,
            PerLine = perLine,
            Total = Round(total, policy),
        };
    }

    private static PromotionOutcome Amount(
        Promotion p, PromotionRule rule, IReadOnlyList<PromoCartLine> cart, List<int> idxs, MoneyPolicy policy)
    {
        var weights = idxs.Select(i => cart[i].Subtotal).ToList();
        var matchSub = weights.Where(w => w > 0).Sum();

        var amt = rule.Value > matchSub ? matchSub : rule.Value;
        var alloc = Allocate(amt, weights, policy);

        var perLine = new Dictionary<int, decimal>();
        for (int j = 0; j < idxs.Count; j++)
        {
            if (alloc[j] <= 0) continue;
            perLine[idxs[j]] = alloc[j];
        }
        return new PromotionOutcome
        {
            PromotionId = p.Id,
            Name = p.Name,
            PerLine = perLine,
            Total = Round(amt, policy),
        };
    }

    /// <summary>Buy-X-get-Y across the whole matching set: every full group of
    /// (buy + get) units frees <c>get</c> units, and the freed ones are the
    /// cheapest in the set. <c>rule.Value</c> is the percent taken off them.</summary>
    private static PromotionOutcome CheapestFree(
        Promotion p, PromotionRule rule, IReadOnlyList<PromoCartLine> cart, List<int> idxs, MoneyPolicy policy)
    {
        var empty = new PromotionOutcome { PromotionId = p.Id, Name = p.Name };
        if (rule.BuyQty is not > 0 || rule.GetQty is not > 0) return empty;

        int buy = rule.BuyQty.Value, get = rule.GetQty.Value;

        // One entry per matching LINE, not per physical unit: all units of a line
        // share a price, so quantity never drives allocation size.
        var lines = new List<(int Index, decimal Price, int Units)>();
        int totalUnits = 0;
        foreach (var i in idxs)
        {
            var n = WholeUnits(cart[i].Quantity);
            if (n <= 0) continue;
            lines.Add((i, cart[i].UnitPrice, n));
            totalUnits += n;
        }
        if (totalUnits <= 0) return empty;

        int groups = totalUnits / (buy + get);
        if (groups <= 0) return empty;

        int free = rule.Repeat ? groups * get : get;
        if (free > totalUnits) free = totalUnits;

        var byPrice = lines.OrderBy(l => l.Price).ToList(); // OrderBy is stable

        var raw = new Dictionary<int, decimal>();
        int remaining = free;
        foreach (var ln in byPrice)
        {
            if (remaining <= 0) break;
            int taken = Math.Min(ln.Units, remaining);
            remaining -= taken;
            raw.TryGetValue(ln.Index, out var acc);
            raw[ln.Index] = acc + taken * ln.Price * rule.Value / 100m;
        }

        var perLine = new Dictionary<int, decimal>();
        decimal total = 0m;
        foreach (var i in idxs)
        {
            if (!raw.TryGetValue(i, out var v)) continue;
            var a = Round(v, policy);
            if (a <= 0) continue;
            perLine[i] = a;
            total += a;
        }
        return new PromotionOutcome
        {
            PromotionId = p.Id,
            Name = p.Name,
            PerLine = perLine,
            Total = Round(total, policy),
        };
    }

    /// <summary>Spreads a total across weights proportionally, rounding each share
    /// and dropping the residual on the last non-zero share so the parts sum
    /// exactly to the rounded total. Zero/negative weights get nothing.</summary>
    private static decimal[] Allocate(decimal total, IReadOnlyList<decimal> weights, MoneyPolicy policy)
    {
        var parts = new decimal[weights.Count];
        decimal sumW = 0m;
        foreach (var w in weights)
            if (w > 0) sumW += w;
        if (sumW <= 0) return parts;

        decimal sum = 0m;
        int last = -1;
        for (int i = 0; i < weights.Count; i++)
        {
            if (weights[i] <= 0) continue;
            parts[i] = Round(total * weights[i] / sumW, policy);
            sum += parts[i];
            if (parts[i] != 0) last = i;
        }

        var target = Round(total, policy);
        if (last == -1)
        {
            if (parts.Length > 0) parts[0] = target;
            return parts;
        }
        parts[last] += target - sum;
        return parts;
    }
}
