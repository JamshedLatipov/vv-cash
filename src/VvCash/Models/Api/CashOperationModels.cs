using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VvCash.Models.Api;

/// <summary>One row of GET /documents/payment/categories/ — the expense heading a
/// cash payout is filed under (rent, wages, and now the exchange payout). The
/// server has no notion of a "default" one, hence the register setting that names
/// which of them an exchange should use.</summary>
public class PaymentCategory
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

// GET /documents/payment/categories/ — the usual envelope around a flat list.
public class PaymentCategoryListResponse
{
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("body")] public List<PaymentCategory>? Body { get; set; }
    [JsonPropertyName("status")] public int Status { get; set; }
}

/// <summary>POST /documents/money/expense/create/ — mirrors the server's
/// cashOpSerializer. <see cref="Counterparty"/> is not optional the way it is on a
/// sale: the sale endpoint falls back to the system counterparty on its own, this
/// one binds the field as a uuid and rejects an empty string.</summary>
public class CashExpenseRequest
{
    [JsonPropertyName("operation_type")] public string OperationType { get; set; } = "expense";
    [JsonPropertyName("cash")] public string Cash { get; set; } = string.Empty;
    [JsonPropertyName("counterparty")] public string Counterparty { get; set; } = string.Empty;
    [JsonPropertyName("note")] public string Note { get; set; } = string.Empty;
    [JsonPropertyName("details")] public List<CashExpenseDetail> Details { get; set; } = new();
}

public class CashExpenseDetail
{
    [JsonPropertyName("payment_category")] public string PaymentCategory { get; set; } = string.Empty;

    /// <summary>Server-side binding is <c>required,gt=0</c> — a zero payout is a 400,
    /// not a no-op, so callers must skip the call rather than send zero.</summary>
    [JsonPropertyName("amount")] public decimal Amount { get; set; }
}

/// <summary>What became of a cash payout. There is no offline queue behind this
/// endpoint, so a failure is final for the press that caused it — and it happens
/// with a return already booked, which is why the reason is carried through to the
/// cashier verbatim instead of being flattened into a boolean.</summary>
public class CashOpOutcome
{
    public bool Success { get; init; }

    /// <summary>The server's own explanation, or the transport error, or null when
    /// neither said anything useful.</summary>
    public string? Message { get; init; }

    public static CashOpOutcome Ok() => new() { Success = true };
    public static CashOpOutcome Failed(string? message) => new() { Success = false, Message = message };
}
