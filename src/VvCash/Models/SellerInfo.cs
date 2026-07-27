using System.Text.Json.Serialization;

namespace VvCash.Models;

/// <summary>A seller on the roster of this cash register, as returned by GET /cashes/seller/.
/// PinHash is cached locally so switching sellers works with no network.</summary>
public class SellerInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("first_name")]
    public string FirstName { get; set; } = string.Empty;

    [JsonPropertyName("last_name")]
    public string LastName { get; set; } = string.Empty;

    [JsonPropertyName("pin_hash")]
    public string PinHash { get; set; } = string.Empty;

    [JsonPropertyName("can_sell")]
    public bool CanSell { get; set; }

    [JsonPropertyName("can_refund")]
    public bool CanRefund { get; set; }

    [JsonPropertyName("can_close_shift")]
    public bool CanCloseShift { get; set; }

    [JsonPropertyName("max_discount")]
    public decimal MaxDiscount { get; set; }

    [JsonIgnore]
    public string FullName => $"{FirstName} {LastName}".Trim();

    [JsonIgnore]
    public bool HasPin => !string.IsNullOrEmpty(PinHash);
}
