using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VvCash.Models;

/// <summary>An auto-applied campaign mirrored from the backend
/// (<c>GET /cashes/promotion/</c>): a target set plus an ordered ladder of
/// quantity rules. Cached locally so the register can price carts offline.</summary>
public class Promotion
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("auto_apply")] public bool AutoApply { get; set; }

    /// <summary>"cart" matches every line; "lines" matches only <see cref="Targets"/>.</summary>
    [JsonPropertyName("apply_scope")] public string ApplyScope { get; set; } = "cart";

    [JsonPropertyName("priority")] public int Priority { get; set; }
    [JsonPropertyName("starts_at")] public DateTimeOffset? StartsAt { get; set; }
    [JsonPropertyName("ends_at")] public DateTimeOffset? EndsAt { get; set; }
    [JsonPropertyName("max_uses")] public int MaxUses { get; set; }
    [JsonPropertyName("used_count")] public int UsedCount { get; set; }

    [JsonPropertyName("targets")] public List<PromotionTarget> Targets { get; set; } = new();

    /// <summary>The ladder, in rung order. The backend serializes rules ordered by
    /// their stored position, so list order IS the ladder order — the first rung a
    /// quantity reaches wins and later rungs never stack on it.</summary>
    [JsonPropertyName("rules")] public List<PromotionRule> Rules { get; set; } = new();
}

public class PromotionTarget
{
    /// <summary>"product" | "category" | "tag".</summary>
    [JsonPropertyName("target_type")] public string TargetType { get; set; } = string.Empty;

    [JsonPropertyName("target_id")] public string TargetId { get; set; } = string.Empty;
}

public class PromotionRule
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    /// <summary>"exact" | "min".</summary>
    [JsonPropertyName("qty_op")] public string QtyOp { get; set; } = string.Empty;

    [JsonPropertyName("qty_from")] public decimal QtyFrom { get; set; }

    /// <summary>"percent" | "amount" | "cheapest_free".</summary>
    [JsonPropertyName("effect")] public string Effect { get; set; } = string.Empty;

    /// <summary>Percent for "percent" and "cheapest_free" (100 = free), an absolute
    /// amount for "amount".</summary>
    [JsonPropertyName("value")] public decimal Value { get; set; }

    [JsonPropertyName("buy_qty")] public int? BuyQty { get; set; }
    [JsonPropertyName("get_qty")] public int? GetQty { get; set; }
    [JsonPropertyName("repeat")] public bool Repeat { get; set; }
}
