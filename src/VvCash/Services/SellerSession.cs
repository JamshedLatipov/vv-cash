using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services;

/// <summary>Tracks who is currently selling at this register. PINs are verified
/// against the locally cached roster, so switching never touches the network.
///
/// The PIN is an attribution guard ("who rang up this sale"), not a security
/// boundary — the device token plus server-side roster validation is the real
/// boundary. This type deliberately adds no hardening beyond what is specified.
///
/// Not thread-safe by design: like <see cref="SessionContext"/>, this is a
/// singleton meant to be driven from the Avalonia UI thread only (PIN entry,
/// register activity). All state here is plain mutable fields/dictionaries with
/// no locking. Every public method that touches state is synchronous under the
/// hood (Task.FromResult/Task.CompletedTask, no awaits), so there is no
/// re-entrancy window even across an `await` — the only actual concurrency
/// concern would be a second caller from a non-UI thread, which nothing in this
/// codebase does today (registration is UI-thread singleton usage, same as
/// SessionContext). Nothing currently violates this — the background sync loop
/// only reaches SellerRosterService/SQLite, never this type — but LoadRosterAsync
/// and SellerRosterService.RefreshAsync are easy to conflate, and a future wiring
/// mistake (e.g. a sync-loop callback invoking LoadRosterAsync directly) would
/// race an unsynchronised `_roster` reassignment against a `SwitchAsync` read with
/// no exception and no reliable repro. Rather than leave that solely to a code
/// comment, every mutating member asserts it is running on the UI thread via
/// <see cref="AssertUiThread"/>. The assert is a Debug-only guard, not a thrown
/// exception: it is compiled out of Release builds entirely (see the assert's own
/// [Conditional("DEBUG")]), so a threading mis-wire is loud in development and in
/// this test suite (Debug configuration) but can never bring down a production
/// register over what is, per the design spec, not a security boundary — trading
/// a slower production diagnosis for zero crash risk on the shop floor. If a
/// background thread ever needs to touch this legitimately, add real
/// synchronization then rather than pre-emptively here.</summary>
public class SellerSession : ISellerSession
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromSeconds(60);

    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _idleTimeout;
    private readonly Dictionary<string, int> _failures = new();
    private readonly Dictionary<string, DateTime> _lockedUntil = new();

    private List<SellerInfo> _roster = new();
    private DateTime _lastActivity;

    public SellerSession() : this(() => DateTime.UtcNow, TimeSpan.FromSeconds(90)) { }

    public SellerSession(Func<DateTime> clock, TimeSpan idleTimeout)
    {
        _clock = clock;
        _idleTimeout = idleTimeout;
        _lastActivity = clock();
    }

    public SellerInfo? Current { get; private set; }

    public IReadOnlyList<SellerInfo> Roster => _roster;

    public bool IsStale => Current == null || _clock() - _lastActivity > _idleTimeout;

    public event EventHandler? CurrentChanged;

    public Task LoadRosterAsync(IEnumerable<SellerInfo> sellers)
    {
        AssertUiThread();
        _roster = sellers.ToList();
        return Task.CompletedTask;
    }

    public Task<SwitchResult> SwitchAsync(string sellerId, string pin)
    {
        AssertUiThread();
        var (result, seller) = Check(sellerId, pin);
        if (result != SwitchResult.Ok) return Task.FromResult(result);

        Current = seller;
        _lastActivity = _clock();
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(SwitchResult.Ok);
    }

    // Deliberately shares Check() (and therefore lockout accounting) with
    // SwitchAsync: an escalation approval (e.g. a supervisor PIN to authorize a
    // refund) is still a PIN guess against that seller's account, and a wrong
    // guess here is exactly as much evidence of PIN-guessing as a wrong guess at
    // the switcher. Exempting ApproveAsync from the counter would open a
    // side channel for unlimited guesses against any seller's PIN via the
    // approval flow instead of the switch flow. It never touches Current or
    // raises CurrentChanged, per the interface contract.
    //
    // The cost of sharing one counter per sellerId: a supervisor who fat-fingers
    // their own PIN twice while approving someone else's refund, then mistypes
    // three more times switching in for their own shift, has burned all five
    // attempts on one combined counter and is locked out of *both* flows for 60
    // seconds — not because they were brute-forced, but because the two flows
    // aren't accounted separately. Accepted trade for now: simpler state, and a
    // supervisor is exactly the seller most likely to also need SwitchAsync
    // shortly after approving, so a shared cool-down isn't a surprising outcome.
    // Revisit with a per-flow counter if that combined lockout proves disruptive
    // in practice.
    public Task<SellerInfo?> ApproveAsync(string sellerId, string pin)
    {
        AssertUiThread();
        var (result, seller) = Check(sellerId, pin);
        return Task.FromResult(result == SwitchResult.Ok ? seller : null);
    }

    public void Touch()
    {
        AssertUiThread();
        _lastActivity = _clock();
    }

    public void Clear()
    {
        AssertUiThread();
        if (Current == null) return;
        Current = null;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    // Debug-only by construction: Debug.Assert carries [Conditional("DEBUG")], so
    // in a Release build this entire call — including the CheckAccess()
    // evaluation — is removed at the call site. See the class remarks for why a
    // silent Release no-op is the right trade-off for a POS terminal.
    private static void AssertUiThread([CallerMemberName] string member = "")
    {
        Debug.Assert(
            Avalonia.Threading.Dispatcher.UIThread.CheckAccess(),
            $"SellerSession.{member} was called off the Avalonia UI thread; this type is not thread-safe (see class remarks).");
    }

    private (SwitchResult, SellerInfo?) Check(string sellerId, string pin)
    {
        var seller = _roster.FirstOrDefault(s => s.Id == sellerId);
        if (seller == null) return (SwitchResult.UnknownSeller, null);
        if (!seller.HasPin) return (SwitchResult.PinNotSet, null);

        if (_lockedUntil.TryGetValue(sellerId, out var until))
        {
            if (_clock() < until) return (SwitchResult.Locked, null);
            _lockedUntil.Remove(sellerId);
            _failures.Remove(sellerId);
        }

        // Only a genuinely wrong PIN counts toward the lockout. A corrupt cached
        // hash would otherwise lock out a seller who typed correctly, for a fault
        // that is not theirs and that retrying cannot clear.
        switch (PinHasher.Verify(pin, seller.PinHash))
        {
            case PinVerificationResult.Malformed:
                return (SwitchResult.CorruptHash, null);

            case PinVerificationResult.WrongPin:
                var count = _failures.TryGetValue(sellerId, out var c) ? c + 1 : 1;
                _failures[sellerId] = count;
                if (count >= MaxFailures) _lockedUntil[sellerId] = _clock() + LockDuration;
                return (SwitchResult.WrongPin, null);
        }

        _failures.Remove(sellerId);
        return (SwitchResult.Ok, seller);
    }
}
