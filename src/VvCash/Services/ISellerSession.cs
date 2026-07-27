using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services;

public enum SwitchResult
{
    /// <summary>The PIN matched and (for <c>SwitchAsync</c>) <c>Current</c> was set.</summary>
    Ok,

    /// <summary>The hash was well-formed and trustworthy, but this PIN did not match
    /// it. Counts toward the seller's lockout.</summary>
    WrongPin,

    /// <summary>This seller has reached <c>MaxFailures</c> wrong guesses within the
    /// last <c>LockDuration</c>. No PIN — not even the correct one — is checked
    /// while locked; retry once the lock expires.</summary>
    Locked,

    /// <summary>This seller has no PIN hash cached (e.g. never set, or the roster
    /// row is incomplete). No PIN can ever succeed until the roster is refreshed
    /// with a real hash. Does not count toward the lockout.</summary>
    PinNotSet,

    /// <summary>No seller with this id is on the currently loaded roster. Does not
    /// count toward any lockout.</summary>
    UnknownSeller,

    /// <summary>The cached hash is unusable — a corrupt row, not a bad guess.
    /// No PIN can succeed until the roster is refreshed.</summary>
    CorruptHash
}

/// <summary>The outcome of an escalation PIN check via <see cref="ISellerSession.ApproveAsync"/>,
/// carrying both the reason (same vocabulary as <see cref="SwitchAsync"/>'s <see cref="SwitchResult"/>)
/// and, on success, the approving seller. Exists so a failed approval can be told apart from a
/// failed switch by <em>why</em> it failed — a bare <c>SellerInfo?</c> collapsed every failure reason
/// into a single null, which meant every failure had to be reported to the cashier as "wrong PIN"
/// even when the true cause (e.g. <see cref="SwitchResult.Locked"/> or <see cref="SwitchResult.CorruptHash"/>)
/// made that message a lie.</summary>
public readonly struct ApprovalResult
{
    public SwitchResult Result { get; }

    /// <summary>The approving seller when <see cref="Result"/> is <see cref="SwitchResult.Ok"/>;
    /// null for every failure reason.</summary>
    public SellerInfo? Approver { get; }

    private ApprovalResult(SwitchResult result, SellerInfo? approver)
    {
        Result = result;
        Approver = approver;
    }

    public static ApprovalResult Success(SellerInfo approver) => new(SwitchResult.Ok, approver);

    public static ApprovalResult Failure(SwitchResult result) => new(result, null);
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
    /// Returns the outcome and, on success, the approving seller — see <see cref="ApprovalResult"/>.</summary>
    Task<ApprovalResult> ApproveAsync(string sellerId, string pin);

    /// <summary>Resets the idle timer — called on any register activity.</summary>
    void Touch();

    void Clear();
}
