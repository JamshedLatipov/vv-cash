// src/VvCash/Models/Api/QuoteModels.cs
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VvCash.Models.Api;

public class QuoteRequest
{
    [JsonPropertyName("warehouse_id")] public string WarehouseId { get; set; } = string.Empty;

    [JsonPropertyName("card_identifier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CardIdentifier { get; set; }

    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }

    [JsonPropertyName("lines")] public List<QuoteLineInput> Lines { get; set; } = new();
}

public class QuoteLineInput
{
    [JsonPropertyName("product_id")] public string ProductId { get; set; } = string.Empty;
    [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
    [JsonPropertyName("unit_price")] public decimal UnitPrice { get; set; }
}

public class QuoteResult
{
    [JsonPropertyName("quote_id")] public string QuoteId { get; set; } = string.Empty;
    [JsonPropertyName("subtotal")] public decimal Subtotal { get; set; }
    [JsonPropertyName("discount_total")] public decimal DiscountTotal { get; set; }
    [JsonPropertyName("total")] public decimal Total { get; set; }
    [JsonPropertyName("lines")] public List<QuoteLineResult> Lines { get; set; } = new();
    [JsonPropertyName("applied")] public List<QuoteApplied> Applied { get; set; } = new();
    [JsonPropertyName("rejected")] public List<QuoteRejected> Rejected { get; set; } = new();
}

public class QuoteLineResult
{
    [JsonPropertyName("product_id")] public string ProductId { get; set; } = string.Empty;
    [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
    [JsonPropertyName("unit_price")] public decimal UnitPrice { get; set; }
    [JsonPropertyName("line_subtotal")] public decimal LineSubtotal { get; set; }
    [JsonPropertyName("discount_amount")] public decimal DiscountAmount { get; set; }
    [JsonPropertyName("discount_percent")] public decimal DiscountPercent { get; set; }
    [JsonPropertyName("final_line_total")] public decimal FinalLineTotal { get; set; }
    [JsonPropertyName("source")] public QuoteSource? Source { get; set; }
}

public class QuoteSource
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("ref")] public string Ref { get; set; } = string.Empty;
}

public class QuoteApplied
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("amount")] public decimal Amount { get; set; }
    [JsonPropertyName("ref")] public string Ref { get; set; } = string.Empty;
}

public class QuoteRejected
{
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    [JsonPropertyName("ref")] public string Ref { get; set; } = string.Empty;
}
