using System;
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

    /// <summary>Unit snapshot: which unit the operator typed in, the factor in
    /// force at the till, and what the line came to in that unit. The server
    /// takes all three or none and rejects a partial trio, so these are set
    /// together or left null together.
    ///
    /// <see cref="Quantity"/> stays in pieces regardless — the trio records how
    /// the amount was entered, not what was sold.
    ///
    /// The factor sent is the one this register synced, not one recomputed at
    /// sale time: the register may have been offline when the card changed, its
    /// receipt is already printed, and the server trusts a cash sale's own
    /// factor for exactly that reason.</summary>
    [JsonPropertyName("unit_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnitId { get; set; }

    [JsonPropertyName("unit_factor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? UnitFactor { get; set; }

    [JsonPropertyName("quantity_in_unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? QuantityInUnit { get; set; }
}

/// <summary>What became of a sale posted to documents/expense/create/.
///
/// <see cref="Queued"/> is not a failure — SyncOfflineDocumentsAsync replays it — but
/// it is not "the server has it" either, and a screen that reports one as the other
/// tells the cashier a document exists that nobody can find yet.
///
/// <see cref="Refused"/> is the third state, and the one that used to be missing: the
/// server understood the document and will not take it. Queueing that (which is what
/// happened before) reported a sale as complete and left it retrying for the life of
/// the register.</summary>
public class ExpenseDocumentOutcome
{
    public bool Posted { get; init; }
    public bool Queued { get; init; }
    public bool Rejected { get; init; }

    /// <summary>The sale's number, from the server. Empty for a queued document, which
    /// has no number until it syncs.</summary>
    public string DocumentNumber { get; init; } = string.Empty;

    /// <summary>Why the server refused it, in its own words. Empty unless
    /// <see cref="Rejected"/>.</summary>
    public string RejectionReason { get; init; } = string.Empty;

    public static ExpenseDocumentOutcome Sent(string documentNumber)
        => new() { Posted = true, DocumentNumber = documentNumber };

    public static ExpenseDocumentOutcome Enqueued() => new() { Queued = true };

    public static ExpenseDocumentOutcome Refused(string reason)
        => new() { Rejected = true, RejectionReason = reason };
}

/// <summary>A sale the register already booked — printed, handed over, cart cleared —
/// that the server then refused on its merits when the queue was replayed.
///
/// This only exists because checkout no longer waits for the server. When the POST was
/// on the interactive path a refusal could be shown as the answer to the press that
/// caused it; now it arrives minutes later, against a receipt already in the customer's
/// hand. <see cref="IExpenseDocumentService.DocumentRejected"/> is what keeps that from
/// being silent — marking the row rejected in SQLite takes it out of the retry rotation
/// (see OfflineStorageService.GetUnsyncedDocumentsAsync) and nothing else reads it, so
/// without this event a paid-for sale would simply never be booked with nobody told.</summary>
public sealed class DocumentRejection : EventArgs
{
    public DocumentRejection(string documentHash, string reason)
    {
        DocumentHash = documentHash;
        Reason = reason;
    }

    /// <summary>The DocumentHash the register generated for the sale — the only handle
    /// the back office has on a document the server never gave a number.</summary>
    public string DocumentHash { get; }

    /// <summary>Why the server refused it, in its own words.</summary>
    public string Reason { get; }
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
