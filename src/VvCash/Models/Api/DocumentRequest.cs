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

    /// <summary>Id of the server quote this sale was priced from. Makes the backend
    /// run FinalizeForSale: price-drift audit plus consuming the winning promo code
    /// or promotion. Null for offline-priced sales, which have no server quote.</summary>
    [JsonPropertyName("quote_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QuoteId { get; set; }

    /// <summary>Promotion this register applied on its own while offline. Sent
    /// instead of <see cref="QuoteId"/> so the backend can still charge the
    /// promotion's usage — otherwise max_uses is ignored for every sale rung up
    /// while disconnected. There is no price-drift audit for such a sale: the
    /// prices were never locked server-side.</summary>
    [JsonPropertyName("offline_promotion_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OfflinePromotionId { get; set; }

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

    /// <summary>Decimal to match the server, which has always taken a float here.
    /// An int truncated weighted goods: 1.4 kg was billed and stock-deducted as 1.</summary>
    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

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

/// <summary>What became of a sale posted to documents/expense/create/.
///
/// <see cref="Queued"/> is not a failure — SyncOfflineDocumentsAsync replays it — but
/// it is not "the server has it" either, and a screen that reports one as the other
/// tells the cashier a document exists that nobody can find yet.</summary>
public class ExpenseDocumentOutcome
{
    public bool Posted { get; init; }
    public bool Queued { get; init; }

    /// <summary>The sale's number, from the server. Empty for a queued document, which
    /// has no number until it syncs.</summary>
    public string DocumentNumber { get; init; } = string.Empty;

    public static ExpenseDocumentOutcome Sent(string documentNumber)
        => new() { Posted = true, DocumentNumber = documentNumber };

    public static ExpenseDocumentOutcome Enqueued() => new() { Queued = true };
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
