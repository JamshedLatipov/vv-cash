using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using VvCash.Models;

namespace VvCash.Services;

public class CartService : ICartService
{
    private readonly ObservableCollection<CartItem> _items = new();
    private readonly ObservableCollection<Coupon> _appliedCoupons = new();

    public IReadOnlyList<CartItem> Items => _items;
    public IReadOnlyList<Coupon> AppliedCoupons => _appliedCoupons;

    public decimal ManualDiscountPercent { get; private set; }
    public decimal ManualDiscountAmount { get; private set; }

    public decimal Subtotal => _items.Sum(i => i.LineTotal);

    public decimal TotalDiscount
    {
        get
        {
            var subtotal = Subtotal;
            // Coupon discounts
            var couponPercent = _appliedCoupons.Sum(c => c.DiscountPercent) / 100m * subtotal;
            var couponFlat = _appliedCoupons.Sum(c => c.DiscountAmount);
            // Manual discount
            var manualPercent = ManualDiscountPercent / 100m * subtotal;
            var manualFlat = ManualDiscountAmount;

            var total = couponPercent + couponFlat + manualPercent + manualFlat;
            // Clamp: discount cannot exceed subtotal
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

    public void ClearManualDiscount()
    {
        ManualDiscountPercent = 0;
        ManualDiscountAmount = 0;
        RaiseCartChanged();
    }

    private void RaiseCartChanged() => CartChanged?.Invoke(this, EventArgs.Empty);
}
