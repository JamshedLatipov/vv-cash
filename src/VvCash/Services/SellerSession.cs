using System;
using System.Collections.Generic;
using System.Linq;
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
/// SessionContext). If a background thread ever needs to touch this, add
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
        _roster = sellers.ToList();
        return Task.CompletedTask;
    }

    public Task<SwitchResult> SwitchAsync(string sellerId, string pin)
    {
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
    public Task<SellerInfo?> ApproveAsync(string sellerId, string pin)
    {
        var (result, seller) = Check(sellerId, pin);
        return Task.FromResult(result == SwitchResult.Ok ? seller : null);
    }

    public void Touch() => _lastActivity = _clock();

    public void Clear()
    {
        if (Current == null) return;
        Current = null;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
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
