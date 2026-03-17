using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VvCash.Models.Api;

public class CounterpartyCreateRequest
{
    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("birthday")]
    public string? Birthday { get; set; }

    [JsonPropertyName("bottom_size")]
    public string? BottomSize { get; set; }

    [JsonPropertyName("credit_limit")]
    public decimal? CreditLimit { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("discount")]
    public CounterpartyDiscount? Discount { get; set; }

    [JsonPropertyName("dynamic_fields")]
    public List<CounterpartyDynamicField>? DynamicFields { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("foot_size")]
    public string? FootSize { get; set; }

    [JsonPropertyName("form")]
    public string? Form { get; set; } = "individual";

    [JsonPropertyName("gender")]
    public string? Gender { get; set; }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("top_size")]
    public string? TopSize { get; set; }
}

public class CounterpartyDiscount
{
    [JsonPropertyName("discount")]
    public decimal Discount { get; set; }

    [JsonPropertyName("discount_type")]
    public string? DiscountType { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("identifier")]
    public string? Identifier { get; set; }
}

public class CounterpartyDynamicField
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}
