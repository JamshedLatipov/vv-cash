using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VvCash.Models.Api;

/// <summary>One row of GET /cashes/remain/ — a stock line for this register's
/// warehouse. Only the two fields reconciliation needs are mapped; the endpoint also
/// returns a name, barcode and article, but reconciliation never inserts products, so
/// there is nothing to build them from and no reason to carry them.
///
/// ProductId comes back empty against the deployed backend today: cashes/cash_repo.go:152
/// never serialises product_id. See SyncService.FetchAllRemainsAsync, the only reader of
/// this type, for the full explanation and what activating this needs.</summary>
public class CashRemainItem
{
    [JsonPropertyName("product_id")] public string ProductId { get; set; } = string.Empty;
    [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
}

/// <summary>The paginated envelope. Note the absence of a "status" field: this endpoint
/// answers with response.List, not the {status, body} shape the rest of the cash API
/// uses, so there is no status to check and page_count is what ends the walk.
///
/// TotalItems is parsed but not currently checked against anything; it is kept as the
/// cross-check a future activator needs for a missing-ORDER-BY pagination risk in
/// GetStockRemains. See SyncService.FetchAllRemainsAsync for the full explanation.</summary>
public class CashRemainPage
{
    [JsonPropertyName("body")] public List<CashRemainItem>? Body { get; set; }
    [JsonPropertyName("page_count")] public int PageCount { get; set; }
    [JsonPropertyName("total_items")] public int TotalItems { get; set; }
}
