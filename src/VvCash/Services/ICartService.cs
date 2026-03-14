using System;
using System.Collections.Generic;
using VvCash.Models;

namespace VvCash.Services;

public interface ICartService
{
    IReadOnlyList<CartItem> Items { get; }
    decimal Subtotal { get; }
    decimal TaxRate { get; }
    decimal Tax { get; }
    decimal TotalDiscount { get; }
    decimal TotalAmount { get; }
    void AddProduct(Product product);
    void RemoveItem(CartItem item);
    void IncreaseQuantity(CartItem item);
    void DecreaseQuantity(CartItem item);
    void ClearCart();
    void ApplyCoupon(Coupon coupon);
    void RemoveCoupon(string code);
    IReadOnlyList<Coupon> AppliedCoupons { get; }
    event EventHandler? CartChanged;
}
