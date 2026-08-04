using System;
using System.Text.Json.Serialization;

namespace VvCash.Models.Api;

public class CounterpartyResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("middle_name")]
    public string? MiddleName { get; set; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }

    [JsonPropertyName("full_name")]
    public string? FullNameRaw { get; set; }

    /// <summary>Falls back to the names the cashier typed when the server's own
    /// full_name comes back blank — seen on the create-counterparty response even
    /// when FirstName/LastName both came back filled. Without the fallback a
    /// freshly created client's name showed up empty in the status line, the
    /// customer chip, and the search list, right after the cashier had typed it
    /// correctly. Mirrors SellerInfo.FullName.</summary>
    [JsonIgnore]
    public string FullName => !string.IsNullOrWhiteSpace(FullNameRaw)
        ? FullNameRaw!
        : $"{FirstName} {LastName}".Trim();

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("gender")]
    public string? Gender { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("shopping_sum")]
    public decimal? ShoppingSum { get; set; }

    [JsonPropertyName("form")]
    public string? Form { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("registration_number")]
    public string? RegistrationNumber { get; set; }

    [JsonPropertyName("birthday")]
    public string? Birthday { get; set; }

    [JsonPropertyName("current_balance")]
    public decimal? CurrentBalance { get; set; }

    // Fallback property because some endpoints return "balance" directly instead of "current_balance"
    [JsonPropertyName("balance")]
    public decimal? Balance
    {
        get => CurrentBalance;
        set => CurrentBalance = value;
    }

    [JsonPropertyName("credit_limit")]
    public decimal? CreditLimit { get; set; }

    [JsonPropertyName("discount_card")]
    public DiscountCardResponse? DiscountCard { get; set; }
}
