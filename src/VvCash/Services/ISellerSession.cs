using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services;

public enum SwitchResult
{
    Ok,
    WrongPin,
    Locked,
    PinNotSet,
    UnknownSeller,

    /// <summary>The cached hash is unusable — a corrupt row, not a bad guess.
    /// No PIN can succeed until the roster is refreshed.</summary>
    CorruptHash
}

/// <summary>Tracks who is currently selling at this register. See <see cref="SellerSession"/>
/// for the implementation and the reasoning behind its lockout rules.</summary>
public interface ISellerSession
{
    SellerInfo? Current { get; }

    /// <summary>True when the idle timeout elapsed and the seller must be re-confirmed.</summary>
    bool IsStale { get; }

    IReadOnlyList<SellerInfo> Roster { get; }

    event EventHandler? CurrentChanged;

    Task LoadRosterAsync(IEnumerable<SellerInfo> sellers);
    Task<SwitchResult> SwitchAsync(string sellerId, string pin);

    /// <summary>Verifies a PIN for an escalation without changing the current seller.
    /// Returns the approving seller on success, null otherwise.</summary>
    Task<SellerInfo?> ApproveAsync(string sellerId, string pin);

    /// <summary>Resets the idle timer — called on any register activity.</summary>
    void Touch();

    void Clear();
}
