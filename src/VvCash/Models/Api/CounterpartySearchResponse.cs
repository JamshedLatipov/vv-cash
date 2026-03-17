using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VvCash.Models.Api;

public class CounterpartySearchResponse
{
    [JsonPropertyName("page_count")]
    public int PageCount { get; set; }

    [JsonPropertyName("total_items")]
    public int TotalItems { get; set; }

    [JsonPropertyName("item_per_page")]
    public int ItemPerPage { get; set; }

    [JsonPropertyName("body")]
    public List<CounterpartyResponse> Body { get; set; } = new();
}
