using System;
using System.Text.Json.Serialization;

namespace VvCash.Models;

/// <summary>How money is rounded, mirrored from the store's settings
/// (<c>GET /cashes/money/</c>). The register needs it because it rounds amounts
/// itself whenever it prices a cart offline; assuming the default would make
/// offline totals differ from the server's for any store configured otherwise.</summary>
public class MoneyPolicy
{
    /// <summary>Decimal places for monetary amounts.</summary>
    [JsonPropertyName("scale")] public int Scale { get; set; } = 2;

    /// <summary>HALF_UP | BANK | UP | DOWN | CEIL | FLOOR.</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "HALF_UP";

    /// <summary>What the server falls back to when a store configured nothing.</summary>
    public static MoneyPolicy Default => new();

    /// <summary>Rounds an amount the way the server would. An unknown mode falls
    /// back to HALF_UP rather than throwing: a bad setting must not stop the
    /// register from ringing up a sale.</summary>
    public decimal Round(decimal value)
    {
        var scale = Math.Clamp(Scale, 0, 28);
        return Mode switch
        {
            "BANK" => Math.Round(value, scale, MidpointRounding.ToEven),
            // UP is away from zero for ANY remainder, not just at the midpoint,
            // so Math.Round(AwayFromZero) is not it — that only breaks ties.
            "UP" => StepAwayFromZero(value, scale),
            "DOWN" => Truncate(value, scale),
            "CEIL" => value > 0 ? StepAwayFromZero(value, scale) : Truncate(value, scale),
            "FLOOR" => value < 0 ? StepAwayFromZero(value, scale) : Truncate(value, scale),
            _ => Math.Round(value, scale, MidpointRounding.AwayFromZero),
        };
    }

    /// <summary>Truncates, then pushes one step further from zero when anything
    /// was cut off.</summary>
    private static decimal StepAwayFromZero(decimal value, int scale)
    {
        var truncated = Truncate(value, scale);
        if (truncated == value) return value;
        var step = 1m / Pow10(scale);
        return value > 0 ? truncated + step : truncated - step;
    }

    private static decimal Truncate(decimal value, int scale)
    {
        var factor = Pow10(scale);
        return decimal.Truncate(value * factor) / factor;
    }

    private static decimal Pow10(int scale)
    {
        decimal factor = 1m;
        for (int i = 0; i < scale; i++) factor *= 10m;
        return factor;
    }
}
