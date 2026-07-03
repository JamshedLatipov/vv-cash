using System;
using System.Collections.Generic;
using VvCash.Models;
using VvCash.Models.Api;

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

    // Customer loyalty card discount
    decimal CustomerDiscountPercent { get; }

    // Server-quoted discount snapshot (null => offline/flat fallback)
    QuoteResult? Quote { get; }
    string? QuoteId { get; }
    void ApplyQuote(QuoteResult result);
    void ClearQuote();

    void AddProduct(Product product);
    void RemoveItem(CartItem item);
    void IncreaseQuantity(CartItem item);
    void DecreaseQuantity(CartItem item);
    void ClearCart();
    void ApplyCoupon(Coupon coupon);
    void RemoveCoupon(string code);
    void SetManualDiscount(decimal percent, decimal amount);
    void ClearManualDiscount();
    void SetCustomerDiscount(decimal percent);
    void ClearCustomerDiscount();
    void LoadSnapshot(
        IEnumerable<CartItem> items,
        decimal manualDiscountPercent, decimal manualDiscountAmount,
        decimal customerDiscountPercent,
        IEnumerable<Coupon> coupons);
    event EventHandler? CartChanged;
}

