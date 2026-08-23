using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VvCash.Models.Api;

/// <summary>One row of GET /cashes/remain/ — a stock line for this register's
/// warehouse. Only the two fields reconciliation needs are mapped; the endpoint also
/// returns a name, barcode and article, but reconciliation never inserts products, so
/// there is nothing to build them from and no reason to carry them.</summary>
public class CashRemainItem
{
    [JsonPropertyName("product_id")] public string ProductId { get; set; } = string.Empty;
    [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
}

/// <summary>The paginated envelope. Note the absence of a "status" field: this endpoint
/// answers with response.List, not the {status, body} shape the rest of the cash API
/// uses, so there is no status to check and page_count is what ends the walk.</summary>
public class CashRemainPage
{
    [JsonPropertyName("body")] public List<CashRemainItem>? Body { get; set; }
    [JsonPropertyName("page_count")] public int PageCount { get; set; }
    [JsonPropertyName("total_items")] public int TotalItems { get; set; }
}
