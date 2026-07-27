using System.Collections.Generic;
using VvCash.Models.Api;

namespace VvCash.Models;

/// <summary>Полный снимок корзины для отложенного чека (хранится как JSON в ParkedSale.Payload).</summary>
public class ParkedSaleSnapshot
{
    public List<ParkedCartItem> Items { get; set; } = new();
    public decimal ManualDiscountPercent { get; set; }
    public decimal ManualDiscountAmount { get; set; }
    public decimal CustomerDiscountPercent { get; set; }
    public List<Coupon> AppliedCoupons { get; set; } = new();
    public CounterpartyResponse? Customer { get; set; }
    public string? Label { get; set; }
}

public class ParkedCartItem
{
    public Product Product { get; set; } = null!;
    public decimal Quantity { get; set; }
}
