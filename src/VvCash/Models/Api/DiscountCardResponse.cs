using System.Text.Json.Serialization;

namespace VvCash.Models.Api;

public class DiscountCardResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("identifier")]
    public string? Identifier { get; set; }

    /// <summary>Discount percentage value (e.g. 10 means 10%).</summary>
    [JsonPropertyName("discount")]
    public decimal Discount { get; set; }

    /// <summary>"progressive" or "static"</summary>
    [JsonPropertyName("discount_type")]
    public string? DiscountType { get; set; }
}
