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

public class ExchangeResponseBody
{
    [JsonPropertyName("return_document_number")] public string? ReturnDocumentNumber { get; set; }
    [JsonPropertyName("expense_document_number")] public string? ExpenseDocumentNumber { get; set; }
    [JsonPropertyName("difference")] public decimal Difference { get; set; }
}
