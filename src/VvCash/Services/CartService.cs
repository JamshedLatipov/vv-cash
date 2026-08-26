using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services.Discounts;

namespace VvCash.Services;

public class CartService : ICartService
{
    private readonly ObservableCollection<CartItem> _items = new();
    private readonly ObservableCollection<Coupon> _appliedCoupons = new();
    private readonly IPromotionProvider _promotionProvider;

    /// <summary>Best local promotion for the current cart, recomputed on every
    /// change. Only consulted when there is no server quote.</summary>
    private PromotionOutcome? _offlinePromotion;

    public CartService(IPromotionProvider promotionProvider)
    {
        _promotionProvider = promotionProvider;
    }

    public IReadOnlyList<CartItem> Items => _items;
    public IReadOnlyList<Coupon> AppliedCoupons => _appliedCoupons;

    public decimal ManualDiscountPercent { get; private set; }
    public decimal ManualDiscountAmount { get; private set; }
    public decimal CustomerDiscountPercent { get; private set; }

    public QuoteResult? Quote { get; private set; }
    public string? QuoteId => Quote?.QuoteId;

    public MoneyPolicy MoneyPolicy => _promotionProvider.MoneyPolicy;

    /// <summary>The local promotion only when it actually drives the cart's
    /// discount: offline, and beating the flat coupon/customer path it competes
    /// with. Gating on the win matters — the sale reports this id to the server,
    /// which charges the promotion's usage against max_uses, so a promotion that
    /// lost must not be billed for a use it never granted.</summary>
    public PromotionOutcome? OfflinePromotion
    {
        get
        {
            if (Quote != null || _offlinePromotion == null) return null;
            return _offlinePromotion.Total > FlatDiscount(Subtotal) ? _offlinePromotion : null;
        }
    }

    /// <summary>Label for the discount actually in force — the server's winning
    /// source online, the locally-picked promotion offline. Null when the discount
    /// comes only from the cashier's manual entry or a flat customer percent.</summary>
    public string? AppliedDiscountName
    {
        get
        {
            if (Quote != null)
            {
                var applied = Quote.Applied.FirstOrDefault();
                if (applied == null) return null;
                return string.IsNullOrWhiteSpace(applied.Name) ? applied.Ref : applied.Name;
            }
            return OfflinePromotion?.Name;
        }
    }

    /// <summary>Rounded to the store's money scale: a line is price × quantity and
    /// a weighed quantity is fractional, so the raw sum can land below the smallest
    /// coin the cashier can actually take.</summary>
    public decimal Subtotal => MoneyPolicy.Round(_items.Sum(i => i.LineTotal));

    /// <summary>The legacy flat path: applied coupons plus the customer's card
    /// percent. Offline this competes with the best local promotion.</summary>
    private decimal FlatDiscount(decimal subtotal)
    {
        var couponPercent = _appliedCoupons.Sum(c => c.DiscountPercent) / 100m * subtotal;
        var couponFlat = _appliedCoupons.Sum(c => c.DiscountAmount);
        var customerPercent = CustomerDiscountPercent / 100m * subtotal;
        return couponPercent + couponFlat + customerPercent;
    }

    public decimal TotalDiscount
    {
        get
        {
            var subtotal = Subtotal;

            decimal baseDiscount;
            if (Quote != null)
            {
                // Server best-deal already includes loyalty/promo/tiers/promotions.
                baseDiscount = Quote.DiscountTotal;
            }
            else
            {
                // Offline: flat path versus the best local promotion, whichever
                // gives more. Mirrors the server's best-deal — one source wins,
                // sources never stack.
                baseDiscount = Math.Max(FlatDiscount(subtotal), _offlinePromotion?.Total ?? 0m);
            }

            // Cashier manual discount always on top.
            var manualPercent = ManualDiscountPercent / 100m * subtotal;
            var manualFlat = ManualDiscountAmount;

            var total = baseDiscount + manualPercent + manualFlat;
            // Rounded like the subtotal, and for the same reason: a percent
            // discount divides, so it produces sub-cent amounts on its own.
            // Leaving them in makes TotalAmount unpayable — the payment screen
            // renders it to two places, the cashier tenders exactly what is on
            // screen, and a remainder nobody can see or hand over keeps the
            // receipt open.
            return MoneyPolicy.Round(Math.Min(total, subtotal));
        }
    }

    public decimal TotalAmount => Subtotal - TotalDiscount;

    public event EventHandler? CartChanged;

    public void AddProduct(Product product)
    {
        var existing = _items.FirstOrDefault(i => i.Product.Id == product.Id);
        if (existing != null)
        {
            existing.Quantity++;
            SyncUnitAmount(existing);
        }
        else
        {
            var item = new CartItem
            {
                Product = product,
                Quantity = 1,
                // A tap adds one piece; the quantity pad is where the cashier
                // states the real amount. The entry mode comes from the product
                // card so tiles open in m² and rolls in pieces.
                EnteredInUnit = product.SellInSecondaryUnit && product.HasSecondaryUnit,
            };
            SyncUnitAmount(item);
            _items.Add(item);
        }
        RaiseCartChanged();
    }

    public void RemoveItem(CartItem item)
    {
        _items.Remove(item);
        RaiseCartChanged();
    }

    /// <summary>Steps by one piece regardless of the entry unit: "+" on a tile
    /// adds a tile, not a square metre. Nobody sells a loose square metre.</summary>
    public void IncreaseQuantity(CartItem item)
    {
        item.Quantity++;
        SyncUnitAmount(item);
        RaiseCartChanged();
    }

    public void DecreaseQuantity(CartItem item)
    {
        if (item.Quantity > 1m)
        {
            item.Quantity--;
            SyncUnitAmount(item);
            RaiseCartChanged();
        }
        else
        {
            RemoveItem(item);
        }
    }

    /// <summary>Sets an exact quantity in pieces — the entry point for weighted
    /// goods, where the amount comes from a scale rather than from +/- taps. A
    /// non-positive quantity removes the line, matching what DecreaseQuantity
    /// does at zero.</summary>
    public void SetQuantity(CartItem item, decimal quantity)
    {
        if (quantity <= 0m)
        {
            RemoveItem(item);
            return;
        }
        item.Quantity = quantity;
        SyncUnitAmount(item);
        RaiseCartChanged();
    }

    /// <summary>Sets the line from an amount in the product's secondary unit —
    /// "12.5 m² of tile" rather than "53 tiles".
    ///
    /// A piece-only product is left alone: there is no factor to convert with,
    /// and inventing one would bill a quantity nobody entered.</summary>
    public void SetQuantityInUnit(CartItem item, decimal amountInUnit)
    {
        if (!item.Product.HasSecondaryUnit) return;

        if (amountInUnit <= 0m)
        {
            // RemoveItem raises CartChanged itself; raising again here would
            // re-price the cart twice per keystroke.
            RemoveItem(item);
            return;
        }

        var (quantity, quantityInUnit) = UnitConverter.ToBase(
            amountInUnit, item.Product.UnitFactor, item.Product.IsDivisible);

        item.Quantity = quantity;
        item.QuantityInUnit = quantityInUnit;
        RaiseCartChanged();
    }

    /// <summary>Brings the unit amount back in line after the piece count moved
    /// on its own — a +/- tap, or a quantity set in pieces. Only ever called
    /// where pieces are authoritative, so recomputing is exactly right; a line
    /// set from a unit amount keeps the figure the cashier typed instead.</summary>
    private static void SyncUnitAmount(CartItem item)
    {
        item.QuantityInUnit = item.Product.HasSecondaryUnit
            ? UnitConverter.ToUnit(item.Quantity, item.Product.UnitFactor)
            : 0m;
    }

    public void ClearCart()
    {
        _items.Clear();
        _appliedCoupons.Clear();
        Quote = null;
        ClearManualDiscount();
        RaiseCartChanged();
    }

    public void ApplyCoupon(Coupon coupon)
    {
        if (!_appliedCoupons.Any(c => c.Code == coupon.Code))
        {
            _appliedCoupons.Add(coupon);
            RaiseCartChanged();
        }
    }

    public void RemoveCoupon(string code)
    {
        var coupon = _appliedCoupons.FirstOrDefault(c => c.Code == code);
        if (coupon != null)
        {
            _appliedCoupons.Remove(coupon);
            RaiseCartChanged();
        }
    }

    public void SetManualDiscount(decimal percent, decimal amount)
    {
        ManualDiscountPercent = percent;
        ManualDiscountAmount = amount;
        RaiseCartChanged();
    }

    public void SetCustomerDiscount(decimal percent)
    {
        CustomerDiscountPercent = percent;
        RaiseCartChanged();
    }

    public void ApplyQuote(QuoteResult result)
    {
        Quote = result;
        ApplyQuotedPrices(result);
        RaiseCartChanged();
    }

    public void ClearQuote()
    {
        Quote = null;
        ApplyQuotedPrices(null);
        RaiseCartChanged();
    }

    /// <summary>Stamps the server's per-line unit price onto the cart, so the screen, the
    /// customer display and the receipt all show what the customer is actually charged
    /// rather than this register's possibly stale cached price. Matched by product id, the
    /// same way <see cref="QuoteLineResolver"/> matches — <see cref="AddProduct"/> merges
    /// repeats of a product onto one line, so the id is unambiguous here. A null result,
    /// or a line the server did not price, falls back to the cached price.</summary>
    private void ApplyQuotedPrices(QuoteResult? result)
    {
        foreach (var item in _items)
        {
            var line = result?.Lines.FirstOrDefault(l => l.ProductId == item.Product.Id);
            item.QuotedUnitPrice = line?.UnitPrice;

            // Per unit, not per line — the same arithmetic ExchangeViewModel.ApplyIssuedQuote
            // does, deliberately kept identical: both screens render this as "what came off
            // this line", and two different answers to that is the defect, not a detail.
            // Null rather than zero when nothing priced the line, so the cart falls back to
            // "no discount" instead of "a discount of nothing".
            item.QuotedUnitDiscount = line != null && line.Quantity > 0
                ? line.DiscountAmount / line.Quantity
                : null;
            item.QuotedDiscountPercent = line?.DiscountPercent ?? 0m;
        }
    }

    public void ClearCustomerDiscount()
    {
        CustomerDiscountPercent = 0;
        RaiseCartChanged();
    }

    public void ClearManualDiscount()
    {
        ManualDiscountPercent = 0;
        ManualDiscountAmount = 0;
        RaiseCartChanged();
    }

    public void LoadSnapshot(
        IEnumerable<CartItem> items,
        decimal manualDiscountPercent, decimal manualDiscountAmount,
        decimal customerDiscountPercent,
        IEnumerable<Coupon> coupons)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(coupons);
        _items.Clear();
        _appliedCoupons.Clear();
        // Quote is intentionally not parked/restored: it is a server-priced
        // snapshot that can go stale. PosViewModel re-fetches it on resume
        // (online) or falls back to the flat customer % (offline).
        Quote = null;
        foreach (var item in items) _items.Add(item);
        // A parked line may carry the price quoted when it was parked; with no quote
        // restored alongside it, that price would outlive the snapshot that justified it.
        ApplyQuotedPrices(null);
        foreach (var coupon in coupons) _appliedCoupons.Add(coupon);
        ManualDiscountPercent = manualDiscountPercent;
        ManualDiscountAmount = manualDiscountAmount;
        CustomerDiscountPercent = customerDiscountPercent;
        RaiseCartChanged();
    }

    private void RaiseCartChanged()
    {
        RecalculateOfflinePromotion();
        ApplyOfflinePromotionToLines();
        CartChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Mirrors the winning offline promotion onto each line's Quoted*
    /// fields — the same fields <see cref="ApplyQuotedPrices"/> sets from a server
    /// quote — so the cart screen's per-line badge (bound to
    /// <see cref="CartItem.HasLineDiscount"/>) agrees with <see cref="TotalDiscount"/>
    /// whichever source is currently winning. Without this, the bottom total and
    /// name show the offline promotion while every line reads as undiscounted,
    /// because nothing else ever writes <see cref="PromotionOutcome.PerLine"/> onto
    /// a <see cref="CartItem"/>. Only touches lines while there is no server quote;
    /// <see cref="ApplyQuote"/>/<see cref="ClearQuote"/> own these fields once one lands.</summary>
    private void ApplyOfflinePromotionToLines()
    {
        if (Quote != null) return;

        var promo = OfflinePromotion; // gated: null unless it actually beats the flat path
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (promo != null && promo.PerLine.TryGetValue(i, out var amount) && item.Quantity > 0)
            {
                item.QuotedUnitDiscount = amount / item.Quantity;
                var subtotal = item.Product.Price * item.Quantity;
                item.QuotedDiscountPercent = subtotal > 0 ? MoneyPolicy.Round(amount / subtotal * 100m) : 0m;
            }
            else
            {
                item.QuotedUnitDiscount = null;
                item.QuotedDiscountPercent = 0m;
            }
        }
    }

    /// <summary>Recomputes the local best-deal promotion. Done once per cart change
    /// rather than inside <see cref="TotalDiscount"/>, which the UI reads repeatedly
    /// per render.</summary>
    private void RecalculateOfflinePromotion()
    {
        if (_items.Count == 0)
        {
            _offlinePromotion = null;
            return;
        }

        var lines = _items.Select(i => new PromoCartLine
        {
            ProductId = i.Product.Id,
            Quantity = i.Quantity,
            UnitPrice = i.Product.Price,
            CategoryId = i.Product.Category,
            TagIds = i.Product.TagIds ?? new List<string>(),
        }).ToList();

        _offlinePromotion = PromotionCalculator.BestDeal(
            _promotionProvider.Promotions, lines, DateTimeOffset.Now, _promotionProvider.MoneyPolicy);
    }
}
