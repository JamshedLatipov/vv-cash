using System.Collections.Generic;

namespace VvCash.Models;

/// <summary>Which cash-register functions this register may use, mirrored from
/// the store's settings (<c>GET /cashes/features/</c>).
///
/// An absent code means enabled. That is deliberate, and it is why the server
/// omits codes it cannot resolve rather than substituting a value of its own:
/// the default lives here and nowhere else, so the two sides cannot drift into
/// disagreeing. It also means a register that has never reached the server —
/// first ever start, no network, or a backend that predates the endpoint — is
/// fully functional rather than locked down, which is the right trade on a shop
/// floor.</summary>
public class CashFeatures
{
    public Dictionary<string, bool> Flags { get; set; } = new();

    /// <summary>What a register with no cached map uses: everything on.</summary>
    public static CashFeatures Default => new();

    public bool IsEnabled(string code) => !Flags.TryGetValue(code, out var enabled) || enabled;
}
