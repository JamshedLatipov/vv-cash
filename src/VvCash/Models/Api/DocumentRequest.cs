using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VvCash.Models.Api;

public class DocumentRequest
{
    [JsonPropertyName("document_hash")]
    public string DocumentHash { get; set; } = string.Empty;

    [JsonPropertyName("seller")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SellerId { get; set; }

    /// <summary>Id of the seller who approved this sale's manual discount when it
    /// exceeded the ringing seller's own cap (see PosViewModel.NeedsDiscountApproval).
    /// Absent for ordinary sales. The backend validates the id and, if the approver no
    /// longer exists, drops it to null and flags the document rather than rejecting the
    /// sale.</summary>
    [JsonPropertyName("approved_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApprovedBy { get; set; }

    [JsonPropertyName("counterparty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Counterparty { get; set; }

    [JsonPropertyName("cash_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CashId { get; set; }

    [JsonPropertyName("shift_id")]
    public string ShiftId { get; set; } = string.Empty;

    [JsonPropertyName("payment")]
    public Payment Payment { get; set; } = new();

    [JsonPropertyName("sold_source")]
    public SoldSourcesEnum SoldSource { get; set; }

    [JsonPropertyName("products")]
    public List<DocumentProduct> Products { get; set; } = new();
}

public class DocumentProduct
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("product_id")]
    public string ProductId { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("invoice_price")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? InvoicePrice { get; set; }

    [JsonPropertyName("sell_price")]
    public decimal SellPrice { get; set; }

    [JsonPropertyName("price_before_discount")]
    public decimal PriceBeforeDiscount { get; set; }

    [JsonPropertyName("discount_percent")]
    public decimal DiscountPercent { get; set; }
}

public enum SoldSourcesEnum
{
    CASH = 1,
    WEB = 2
}

public class Payment
{
    [JsonPropertyName("to_pay")]
    public decimal ToPay { get; set; }

    [JsonPropertyName("paid_in_cash")]
    public decimal PaidInCash { get; set; }

    [JsonPropertyName("paid_by_credit_card")]
    public decimal PaidByCreditCard { get; set; }

    [JsonPropertyName("discount_type")]
    public string DiscountType { get; set; } = "percent"; // 'percent' | 'cash'

    [JsonPropertyName("discount")]
    public decimal Discount { get; set; }

    [JsonPropertyName("remained")]
    public decimal Remained { get; set; }
}
