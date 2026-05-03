using System;
using System.Collections.Generic;
using VvCash.Models;

namespace VvCash.Services;

public interface ICartService
{
    IReadOnlyList<CartItem> Items { get; }
    decimal Subtotal { get; }

    // Coupon discounts
    decimal TotalDiscount { get; }
    decimal TotalAmount { get; }
    IReadOnlyList<Coupon> AppliedCoupons { get; }

    // Manual discount set by cashier
    decimal ManualDiscountPercent { get; }
    decimal ManualDiscountAmount { get; }

    void AddProduct(Product product);
    void RemoveItem(CartItem item);
    void IncreaseQuantity(CartItem item);
    void DecreaseQuantity(CartItem item);
    void ClearCart();
    void ApplyCoupon(Coupon coupon);
    void RemoveCoupon(string code);
    void SetManualDiscount(decimal percent, decimal amount);
    void ClearManualDiscount();
    event EventHandler? CartChanged;
}

