using System.Text.Json.Serialization;

namespace VvCash.Models.Api;

public class CounterpartyResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }
}
