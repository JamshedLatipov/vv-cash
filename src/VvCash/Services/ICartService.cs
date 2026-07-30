using System;
using System.Collections.Generic;
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services.Discounts;

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

    // Locally computed promotion, used only while there is no server quote.
    PromotionOutcome? OfflinePromotion { get; }

    // Display label of the discount source currently in force (null if none).
    string? AppliedDiscountName { get; }

    // Store money rounding, used wherever the register computes money itself.
    MoneyPolicy MoneyPolicy { get; }

    void AddProduct(Product product);
    void RemoveItem(CartItem item);
    void IncreaseQuantity(CartItem item);
    void DecreaseQuantity(CartItem item);
    void SetQuantity(CartItem item, decimal quantity);
    void SetQuantityInUnit(CartItem item, decimal amountInUnit);
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

