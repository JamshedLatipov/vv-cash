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

    public decimal Subtotal => _items.Sum(i => i.LineTotal);

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
            return Math.Min(total, subtotal);
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
        }
        else
        {
            _items.Add(new CartItem { Product = product, Quantity = 1 });
        }
        RaiseCartChanged();
    }

    public void RemoveItem(CartItem item)
    {
        _items.Remove(item);
        RaiseCartChanged();
    }

    public void IncreaseQuantity(CartItem item)
    {
        item.Quantity++;
        RaiseCartChanged();
    }

    public void DecreaseQuantity(CartItem item)
    {
        if (item.Quantity > 1m)
        {
            item.Quantity--;
            RaiseCartChanged();
        }
        else
        {
            RemoveItem(item);
        }
    }

    /// <summary>Sets an exact quantity — the entry point for weighted goods, where
    /// the amount comes from a scale rather than from +/- taps. A non-positive
    /// quantity removes the line, matching what DecreaseQuantity does at zero.</summary>
    public void SetQuantity(CartItem item, decimal quantity)
    {
        if (quantity <= 0m)
        {
            RemoveItem(item);
            return;
        }
        item.Quantity = quantity;
        RaiseCartChanged();
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
            item.QuotedUnitPrice = result?.Lines.FirstOrDefault(l => l.ProductId == item.Product.Id)?.UnitPrice;
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
        CartChanged?.Invoke(this, EventArgs.Empty);
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
