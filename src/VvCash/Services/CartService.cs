using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using VvCash.Models;
using VvCash.Models.Api;

namespace VvCash.Services;

public class CartService : ICartService
{
    private readonly ObservableCollection<CartItem> _items = new();
    private readonly ObservableCollection<Coupon> _appliedCoupons = new();

    public IReadOnlyList<CartItem> Items => _items;
    public IReadOnlyList<Coupon> AppliedCoupons => _appliedCoupons;

    public decimal ManualDiscountPercent { get; private set; }
    public decimal ManualDiscountAmount { get; private set; }
    public decimal CustomerDiscountPercent { get; private set; }

    public QuoteResult? Quote { get; private set; }
    public string? QuoteId => Quote?.QuoteId;

    public decimal Subtotal => _items.Sum(i => i.LineTotal);

    public decimal TotalDiscount
    {
        get
        {
            var subtotal = Subtotal;

            decimal baseDiscount;
            if (Quote != null)
            {
                // Server best-deal already includes loyalty/promo/tiers.
                baseDiscount = Quote.DiscountTotal;
            }
            else
            {
                // Offline / no card: legacy flat path.
                var couponPercent = _appliedCoupons.Sum(c => c.DiscountPercent) / 100m * subtotal;
                var couponFlat = _appliedCoupons.Sum(c => c.DiscountAmount);
                var customerPercent = CustomerDiscountPercent / 100m * subtotal;
                baseDiscount = couponPercent + couponFlat + customerPercent;
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
        if (item.Quantity > 1)
        {
            item.Quantity--;
            RaiseCartChanged();
        }
        else
        {
            RemoveItem(item);
        }
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
        RaiseCartChanged();
    }

    public void ClearQuote()
    {
        Quote = null;
        RaiseCartChanged();
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
        foreach (var coupon in coupons) _appliedCoupons.Add(coupon);
        ManualDiscountPercent = manualDiscountPercent;
        ManualDiscountAmount = manualDiscountAmount;
        CustomerDiscountPercent = customerDiscountPercent;
        RaiseCartChanged();
    }

    private void RaiseCartChanged() => CartChanged?.Invoke(this, EventArgs.Empty);
}
