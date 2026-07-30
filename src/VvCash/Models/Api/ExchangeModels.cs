using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VvCash.Models.Api;

// POST /documents/exchange/{expenseDocumentBaseId}/
public class ExchangeRequest
{
    [JsonPropertyName("document_hash")] public string DocumentHash { get; set; } = string.Empty;
    [JsonPropertyName("selected_date")] public string SelectedDate { get; set; } = string.Empty;
    [JsonPropertyName("returned")] public List<ReturnLineRequest> Returned { get; set; } = new();
    [JsonPropertyName("issued")] public DocumentRequest Issued { get; set; } = new();
    [JsonPropertyName("difference_payment")] public ExchangeDifferencePayment DifferencePayment { get; set; } = new();
}

public class ExchangeDifferencePayment
{
    [JsonPropertyName("paid_in_cash")] public decimal PaidInCash { get; set; }
    [JsonPropertyName("paid_by_credit_card")] public decimal PaidByCreditCard { get; set; }
}

public class ExchangeResponse
{
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("body")] public ExchangeResponseBody? Body { get; set; }
    [JsonPropertyName("status")] public int Status { get; set; }
}

/// <summary>What came back from an exchange attempt, or what did not.
///
/// <see cref="Body"/> is non-null only when the server actually booked the
/// exchange — that is the "null means refused" contract callers rely on before
/// printing anything. The other two fields exist so a refusal can be told apart
/// from silence: the endpoint answers 400 for an expired window or a total that
/// does not add up and 409 for an exchange already processed, each with its own
/// reason, while a dead network leaves <see cref="StatusCode"/> null because
/// nothing was answered at all.</summary>
public class ExchangeOutcome
{
    public ExchangeResponseBody? Body { get; init; }

    /// <summary>HTTP status the server answered with; null when it never answered.</summary>
    public int? StatusCode { get; init; }

    /// <summary>The server's own explanation, shown to the cashier as-is.</summary>
    public string? Message { get; init; }
}

public class ExchangeResponseBody
{
    [JsonPropertyName("return_document_number")] public string? ReturnDocumentNumber { get; set; }
    [JsonPropertyName("expense_document_number")] public string? ExpenseDocumentNumber { get; set; }
    [JsonPropertyName("difference")] public decimal Difference { get; set; }
}
