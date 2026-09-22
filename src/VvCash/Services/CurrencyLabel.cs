using System;

namespace VvCash.Services;

/// <summary>Turns the shop's ISO currency code into the short form a cashier reads
/// beside an amount: "TJS" becomes "смн.".
///
/// The spelling lives in the locale files under <c>Currency_&lt;CODE&gt;</c>, because
/// it is a matter of language — the same somoni is "смн." in Russian and would be
/// something else in Uzbek Latin. A code no locale knows is shown as it is: "AED"
/// is still true, where a guessed word would not be.</summary>
public static class CurrencyLabel
{
    /// <summary>Label for <paramref name="code"/> in the current UI language, or null
    /// when the shop's currency is unknown.</summary>
    public static string? For(string? code)
        => For(code, key => I18nService.Instance.TryGet(key, out var value) ? value : null);

    internal static string? For(string? code, Func<string, string?> lookup)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var normalized = code.Trim().ToUpperInvariant();
        return lookup($"Currency_{normalized}") ?? normalized;
    }
}
