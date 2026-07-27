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

    /// <summary>Id of the seller who approved <see cref="ManualDiscountPercent"/> when it
    /// exceeded the ringing seller's own cap (mirrors PosViewModel's own _approvedById —
    /// see its remarks). Carried here so an approval that already happened survives
    /// park→resume instead of being silently dropped; resuming must never re-prompt for
    /// an approval a supervisor already gave. Null both when the discount never needed
    /// approval and — deliberately, via System.Text.Json's default-on-missing-property
    /// behaviour — when this snapshot was parked by a build that predates this field, so
    /// an old parked sale resumes cleanly with no approver rather than crashing or
    /// fabricating one.</summary>
    public string? ApprovedById { get; set; }
}

public class ParkedCartItem
{
    public Product Product { get; set; } = null!;
    public decimal Quantity { get; set; }
}
