using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Api;

namespace VvCash.ViewModels;

/// <summary>Drives the seller-switch overlay. Two modes: switching who is currently
/// selling (<see cref="Open"/>), and approving an operation the current seller lacks
/// the right for (<see cref="OpenForApproval"/>) — the latter never changes
/// <see cref="ISellerSession.Current"/>, it only verifies a PIN and reports who
/// entered it via <see cref="Approved"/>.
///
/// Flow: a grid of name tiles (<see cref="Sellers"/>) -> tap one
/// (<see cref="SelectSellerCommand"/>) -> a 4-digit PIN pad
/// (<see cref="AppendDigitCommand"/>) that auto-submits on the fourth digit -> the
/// overlay closes on success.
///
/// A third case lives inside the switch flow specifically (never approval — see
/// <see cref="BeginPinSetup"/>): a seller whose PIN was never set
/// (<see cref="SwitchResult.PinNotSet"/>) gets to create one on the spot instead of
/// being shown an error — see the "PIN setup (Task 19)" region below.</summary>
public partial class SellerSwitchViewModel : ViewModelBase, IDisposable
{
    private const int PinLength = 4;

    private readonly ISellerSession _session;
    private readonly ISellerRosterService _rosterService;

    /// <summary>The continuation for the approval currently in flight (or about to be),
    /// set by <see cref="OpenForApproval"/> and consumed exactly once — by a successful
    /// PIN in <see cref="SubmitAsync"/>, or discarded by <see cref="Cancel"/>. Every call
    /// to <see cref="Show"/> (both <see cref="Open"/> and <see cref="OpenForApproval"/>)
    /// overwrites this, so each approval owns only its own follow-up: a cancelled or
    /// superseded approval can never cause a later, unrelated one to run the wrong
    /// operation — there is only ever one slot, and it always reflects the most recent
    /// open call. This replaces an earlier design where every caller shared one
    /// <see cref="Approved"/> event and guarded it with its own boolean "is this approval
    /// for me" flag, which does not scale past a single flow (two flags can both end up
    /// armed at once) and cannot be cleared by a cancel that this class didn't know had
    /// happened.</summary>
    private Func<SellerInfo, Task>? _onApproved;

    // Set for the duration of SubmitAsync (and, for the PIN-setup flow, its own
    // network round-trip) and checked by every other entry point that mutates
    // overlay state (SelectSeller, Back, a fresh Show via Open/OpenForApproval).
    // SellerSession's Task-returning members complete synchronously (see its class
    // remarks), so for the plain switch/approval path the dispatcher never actually
    // pumps other input mid-submit and this guard rarely trips in practice there.
    // It is NOT hypothetical for PIN setup, though: SetPinAsync (see
    // SubmitPinSetupStepAsync) is genuine network I/O with a real suspension point,
    // so without this guard a stray tap on "Back" or a fresh Open() while that
    // request is in flight could hide/reset the overlay — or start a second,
    // overlapping setup attempt — out from under the still-pending call.
    private bool _isBusy;

    /// <summary>The caller's permission for whether the manual sign-out control may be
    /// offered at all — see <see cref="Open"/>'s canSignOut parameter and
    /// <see cref="CanSignOut"/>. Assigned inside <see cref="Show"/>'s _isBusy guard for
    /// the same reason <see cref="_onApproved"/> is (see Show's remarks): a call that
    /// arrives while a submit is genuinely in flight must not overwrite state for that
    /// still-pending submit.</summary>
    private bool _callerAllowsSignOut = true;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isPinEntry;
    [ObservableProperty] private SellerInfo? _selectedSeller;
    [ObservableProperty] private string _pin = string.Empty;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = string.Empty;

    /// <summary>True while the PIN pad is being used to create (rather than verify) a
    /// PIN — see the "PIN setup (Task 19)" region. Combine with
    /// <see cref="IsConfirmingNewPin"/> via <see cref="IsCreatingNewPin"/>/
    /// <see cref="IsRepeatingNewPin"/> to pick the right prompt.</summary>
    [ObservableProperty] private bool _isSettingPin;

    /// <summary>True on the second ("repeat it") step of PIN setup; false on the
    /// first. Meaningless unless <see cref="IsSettingPin"/> is also true.</summary>
    [ObservableProperty] private bool _isConfirmingNewPin;

    /// <summary>The first entry of a new PIN, held only long enough to compare
    /// against the second — see <see cref="SubmitPinSetupStepAsync"/>.</summary>
    private string? _pendingNewPin;

    /// <summary>Drives the "create a PIN" prompt (first entry of PIN setup).</summary>
    public bool IsCreatingNewPin => IsSettingPin && !IsConfirmingNewPin;

    /// <summary>Drives the "repeat it" prompt (second entry of PIN setup).</summary>
    public bool IsRepeatingNewPin => IsSettingPin && IsConfirmingNewPin;

    partial void OnIsSettingPinChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCreatingNewPin));
        OnPropertyChanged(nameof(IsRepeatingNewPin));
    }

    partial void OnIsConfirmingNewPinChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCreatingNewPin));
        OnPropertyChanged(nameof(IsRepeatingNewPin));
    }

    /// <summary>True while the overlay is verifying a PIN to approve an operation on
    /// behalf of the current seller (see <see cref="OpenForApproval"/>); false while it
    /// is switching who the current seller is (see <see cref="Open"/>). The view binds
    /// this to choose its heading — "Confirm with PIN" for approval vs. "Who is
    /// selling?" for switching — since asking a supervisor who is authorising someone
    /// else's operation "who is selling?" is the wrong question.</summary>
    [ObservableProperty] private bool _isApprovalMode;

    public ObservableCollection<SellerInfo> Sellers { get; } = new();

    /// <summary>True when an approval was requested but nobody on the roster holds the
    /// right being escalated (see <see cref="OpenForApproval"/>) — e.g. opening returns
    /// on a register where no seller has the refund permission. Without a notice for
    /// this the overlay is a dead end: the tile grid renders zero tiles, the PIN pad
    /// stays hidden (nothing can be selected), and all the cashier sees is a heading
    /// and a close button over an empty card, with no hint that the missing piece is a
    /// permission grant rather than a bug. The view still keeps its close control, so
    /// the notice explains the dismissal rather than blocking it.</summary>
    public bool HasNoApprover => IsApprovalMode && Sellers.Count == 0;

    /// <summary>True when a plain seller switch found an empty roster — this register
    /// has no sellers assigned, or the roster has never been loaded/cached (offline
    /// first run). Distinct from <see cref="HasNoApprover"/> because the remedy is
    /// different: assign sellers to the register / let the roster sync, rather than
    /// grant a permission. Cancelling here is harmless — sales fall back to crediting
    /// the shift owner, see <see cref="Cancel"/>.</summary>
    public bool HasEmptyRoster => !IsApprovalMode && Sellers.Count == 0;

    /// <summary>True when the tile-grid screen should offer a manual "stop selling"
    /// control — the way for the person at the register to explicitly become nobody,
    /// without switching to someone else. Three conditions, all required:
    ///  - Never in approval mode (<see cref="IsApprovalMode"/>): an approval verifies a
    ///    supervisor's PIN on someone else's behalf and deliberately never touches
    ///    <see cref="ISellerSession.Current"/> — see the class-level remarks — so a
    ///    sign-out control there would be nonsense.
    ///  - Only when the caller allowed it (<see cref="_callerAllowsSignOut"/>, set by
    ///    <see cref="Open"/>'s canSignOut parameter). PosViewModel sources this from
    ///    <c>CanEndSellerSession</c> — the same cart-empty rule its own EndReceipt
    ///    guards on, since dropping the seller mid-receipt would leave the rest of it
    ///    with nobody confirmed and nothing to re-prompt (AddToCart's gate only re-asks
    ///    on an EMPTY cart).
    ///  - Only once somebody is actually <see cref="ISellerSession.Current"/> — nothing
    ///    to sign out of otherwise.</summary>
    public bool CanSignOut => !IsApprovalMode && _callerAllowsSignOut && _session.Current != null;

    partial void OnIsApprovalModeChanged(bool value) => NotifyEmptyStateChanged();

    private void NotifyEmptyStateChanged()
    {
        OnPropertyChanged(nameof(HasNoApprover));
        OnPropertyChanged(nameof(HasEmptyRoster));
        OnPropertyChanged(nameof(CanSignOut));
    }

    /// <summary>Raised when an escalation PIN was accepted, carrying the approving seller.</summary>
    public event EventHandler<SellerInfo>? Approved;

    public SellerSwitchViewModel(ISellerSession session, ISellerRosterService rosterService)
    {
        _session = session;
        _rosterService = rosterService;

        // Driven off the collection itself rather than a line at the end of Show():
        // Show() rebuilds Sellers in two steps (Clear then Add per seller), so the
        // empty-state notices must track the collection's real content at all times,
        // not just whatever it happened to hold when the last explicit notification
        // was raised.
        Sellers.CollectionChanged += (_, _) => NotifyEmptyStateChanged();

        // CanSignOut reads _session.Current directly, so it must also react when Current
        // changes for a reason outside this class's own SignOutSeller/SubmitAsync calls —
        // in particular SellerSession.LoadRosterAsync clearing Current when the seller a
        // still-open overlay is showing has vanished from the roster or lost CanSell. Kept
        // alive for as long as this VM is, so see Dispose for why unsubscribing matters:
        // _session is a singleton and this VM is transient.
        _session.CurrentChanged += OnSessionCurrentChanged;
    }

    private void OnSessionCurrentChanged(object? sender, EventArgs e)
        => OnPropertyChanged(nameof(CanSignOut));

    /// <summary>Unsubscribes from <see cref="ISellerSession.CurrentChanged"/> — required
    /// because <see cref="ISellerSession"/> is a singleton and this view model is
    /// transient (a fresh one is resolved per <c>NavigateToPos</c> in App.axaml.cs, same
    /// as <see cref="PosViewModel"/>): without this, every logout/login cycle would leave
    /// one more dead instance permanently subscribed, still reacting to events on a VM
    /// nothing displays any more. Mirrors <c>PosViewModel.Dispose</c>'s own reasoning for
    /// the identical problem.</summary>
    public void Dispose() => _session.CurrentChanged -= OnSessionCurrentChanged;

    /// <summary>Opens the overlay to switch the current seller. Lists the whole roster.
    /// <paramref name="canSignOut"/> is the caller's permission for whether the manual
    /// sign-out control should be offered at all this time (see <see cref="CanSignOut"/>)
    /// — defaults to <c>false</c>, not <c>true</c>: after the fix for the raise-site bug
    /// (see <see cref="SellerSwitchRequest"/>'s own remarks), the rule is that only a
    /// caller which actually checked its own permission may grant this, so the permissive
    /// value must never be what a forgotten argument silently produces. PosViewModel's
    /// <c>OpenSellerSwitch</c> is the one caller that passes its own
    /// <c>CanEndSellerSession</c> (true only on an empty cart, for the same reason
    /// EndReceipt itself is guarded there) — every other caller either omits the argument
    /// on purpose or passes <c>false</c> explicitly.</summary>
    public void Open(bool canSignOut = false) => Show(_ => true, approvalMode: false, canSignOut: canSignOut);

    /// <summary>Opens the overlay to approve an operation the current seller lacks
    /// <paramref name="hasRight"/> for. Lists only sellers holding that right, and a
    /// successful PIN here never changes <see cref="ISellerSession.Current"/> — it
    /// raises <see cref="Approved"/> and, if given, invokes <paramref name="onApproved"/>
    /// with the approving seller. <paramref name="onApproved"/> is how the caller's own
    /// operation actually resumes — see <see cref="_onApproved"/> for why each call owns
    /// its own continuation instead of every caller sharing one event. Passes
    /// <c>canSignOut: false</c> explicitly as defence in depth — <see cref="CanSignOut"/>'s
    /// own <c>!IsApprovalMode</c> check already rules the control out regardless of what
    /// gets passed here, but this way the rule holds even if that check is ever removed
    /// or narrowed.</summary>
    public void OpenForApproval(Func<SellerInfo, bool> hasRight, Func<SellerInfo, Task>? onApproved = null)
        => Show(hasRight, approvalMode: true, onApproved, canSignOut: false);

    private void Show(Func<SellerInfo, bool> filter, bool approvalMode, Func<SellerInfo, Task>? onApproved = null, bool canSignOut = false)
    {
        // A fresh Open()/OpenForApproval() while a submit is in flight would
        // reset SelectedSeller/Pin/Sellers out from under SubmitAsync's still-
        // pending continuation. Ignore it — the in-flight submit resolves the
        // overlay itself (hides on success, shows an error on failure) shortly;
        // the caller can open again afterwards if still needed.
        if (_isBusy) return;

        // Assigned here, inside the same busy guard as everything else Show() resets,
        // rather than by the public Open()/OpenForApproval() methods before calling
        // Show(): otherwise a call that arrives while a submit is genuinely in flight
        // would overwrite _onApproved for that still-pending submit even though Show()
        // itself goes on to no-op. Open() always passes null, which is what clears a
        // stale continuation left behind by an approval that opened but was then
        // cancelled or superseded rather than completed. _callerAllowsSignOut is
        // assigned the same way and for the same reason.
        _onApproved = onApproved;
        _callerAllowsSignOut = canSignOut;

        IsApprovalMode = approvalMode;

        Sellers.Clear();
        foreach (var seller in _session.Roster.Where(filter))
            Sellers.Add(seller);

        // Reset everything so a previous failed attempt (wrong PIN, corrupt hash,
        // a half-finished PIN-setup, etc.) never leaks into the next time the
        // overlay opens.
        SelectedSeller = null;
        Pin = string.Empty;
        HasError = false;
        ErrorMessage = string.Empty;
        IsPinEntry = false;
        IsSettingPin = false;
        IsConfirmingNewPin = false;
        _pendingNewPin = null;
        IsVisible = true;

        // Sellers.CollectionChanged (above) already re-raises HasNoApprover/HasEmptyRoster/
        // CanSignOut on every Show() call, since Sellers.Clear() unconditionally raises a
        // Reset regardless of whether the collection had anything in it — so relying on
        // that alone would happen to work here too. Notified explicitly anyway: canSignOut
        // is state this method owns outright (assigned two lines up, from its own
        // parameter), not something derived from the roster, so its notification
        // shouldn't ride along as an incidental side effect of Sellers being rebuilt —
        // that coupling is exactly the kind of thing a future refactor of the roster
        // rebuild (say, replacing Clear()+Add() with reassigning Sellers wholesale) could
        // break without anyone noticing CanSignOut stopped updating.
        NotifyEmptyStateChanged();
    }

    [RelayCommand]
    private void SelectSeller(SellerInfo seller)
    {
        if (_isBusy) return;

        SelectedSeller = seller;
        Pin = string.Empty;
        HasError = false;
        ErrorMessage = string.Empty;
        IsPinEntry = true;
        // A fresh selection is never mid PIN-setup for whichever seller was
        // selected before — see BeginPinSetup for why this state must not leak
        // from one selected seller to another.
        IsSettingPin = false;
        IsConfirmingNewPin = false;
        _pendingNewPin = null;
    }

    [RelayCommand]
    private async Task AppendDigitAsync(string digit)
    {
        // The Pin.Length >= PinLength half of this guard is what actually
        // prevents a double submit today, while SellerSession's Task-returning
        // members still complete synchronously (see its class remarks): a second
        // call only ever arrives after the first one already ran to completion,
        // by which point Pin is already full. The _isBusy check is the one that
        // matters once that stops being true.
        if (_isBusy || SelectedSeller == null || Pin.Length >= PinLength) return;

        HasError = false;
        Pin += digit;

        if (Pin.Length == PinLength)
            await SubmitAsync();
    }

    [RelayCommand]
    private void Backspace()
    {
        if (Pin.Length > 0) Pin = Pin[..^1];
        HasError = false;
    }

    /// <summary>Returns from the PIN pad to the tile grid without closing the overlay.</summary>
    [RelayCommand]
    private void Back()
    {
        if (_isBusy) return;

        IsPinEntry = false;
        SelectedSeller = null;
        Pin = string.Empty;
        HasError = false;
        ErrorMessage = string.Empty;
        IsSettingPin = false;
        IsConfirmingNewPin = false;
        _pendingNewPin = null;
    }

    /// <summary>Dismisses the overlay entirely without completing whatever it was opened
    /// for — reachable from the tile grid (a visible close control, see
    /// SellerSwitchView.axaml) and, via Escape, from either state. What "cancel" means
    /// depends on the mode it interrupts:
    ///  - Approval (<see cref="IsApprovalMode"/>): abandons the operation that requested
    ///    it. <see cref="_onApproved"/> is discarded before hiding, so nothing runs — and
    ///    because each approval owns its own continuation slot (see its remarks), a later,
    ///    unrelated approval can never be mistaken for permission to run this one.
    ///  - Switch: leaves <see cref="ISellerSession.Current"/> exactly as it was. If nobody
    ///    was ever selected, the register keeps working and sales fall back to the shift
    ///    owner (see <see cref="ISellerSession"/>'s offline-degradation remarks) — that is
    ///    the designed behaviour, not an error this needs to prevent.
    /// A no-op while <see cref="_isBusy"/>, same as every other entry point that mutates
    /// overlay state: an in-flight PIN check or PIN-setup network call must resolve on its
    /// own rather than be yanked out from under by a cancel tap.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (_isBusy) return;

        _onApproved = null;
        HideAndReset();
    }

    /// <summary>The manual counterpart to <c>PosViewModel.EndReceipt</c>: explicitly stops
    /// being the current seller — "nobody is selling now" — without switching to anyone
    /// else. Only ever reachable while <see cref="CanSignOut"/> is true (see its remarks
    /// for the three conditions that gate it, in particular that this never runs in
    /// approval mode), but this method does not re-check that itself: like
    /// <see cref="SelectSeller"/>/<see cref="Back"/>/<see cref="Cancel"/>, it trusts the
    /// view to only invoke it when the bound control is actually visible.
    ///
    /// Mirrors <see cref="Cancel"/>'s dismissal exactly — same <see cref="_onApproved"/>
    /// discard (harmless here in practice, since reaching this while <see cref="CanSignOut"/>
    /// is true already implies <see cref="IsApprovalMode"/> is false and therefore
    /// <see cref="_onApproved"/> is already null, but kept for the same defensive
    /// not-this-class's-job-to-assume-that reason as <see cref="Cancel"/>) and the same
    /// <see cref="HideAndReset"/> — plus the one thing Cancel deliberately never does:
    /// actually clearing <see cref="ISellerSession.Current"/>. A no-op while
    /// <see cref="_isBusy"/>, same as every other mutating entry point in this class.</summary>
    // Named SignOutSeller, not SignOut: PosViewModel already has its own SignOutCommand
    // meaning "log out of the app entirely and navigate away". Same bare name on two
    // view models with materially different blast radius is exactly the kind of thing
    // that gets mis-wired later — unlike Cancel/Back, which really are the same concept
    // reused per view, these two are not.
    [RelayCommand]
    private void SignOutSeller()
    {
        if (_isBusy) return;

        _onApproved = null;
        _session.Clear();
        HideAndReset();
    }

    private async Task SubmitAsync()
    {
        if (SelectedSeller == null) return;
        var sellerId = SelectedSeller.Id;

        if (IsSettingPin)
        {
            await SubmitPinSetupStepAsync(sellerId);
            return;
        }

        _isBusy = true;
        try
        {
            if (IsApprovalMode)
            {
                // ApproveAsync now surfaces the same SwitchResult vocabulary as
                // SwitchAsync (see ApprovalResult), so both modes share one
                // result-to-message mapping instead of the approval path
                // collapsing every failure into a generic "wrong PIN". PinNotSet is
                // NOT routed into PIN setup here — see BeginPinSetup's remarks for
                // why that flow is scoped to the plain switch below only.
                var approval = await _session.ApproveAsync(sellerId, Pin);
                if (approval.Result != SwitchResult.Ok)
                {
                    Fail(MessageFor(approval.Result));
                    return;
                }

                // Result == Ok guarantees Approver is set — see ApprovalResult.Success.
                var approver = approval.Approver!;

                // Consumed (and cleared) here, before Succeed()/Approved fire: a
                // continuation that itself opens another approval (unlikely today, but
                // not this class's job to rule out) must not find its own call still
                // sitting in _onApproved.
                var continuation = _onApproved;
                _onApproved = null;

                Succeed();
                Approved?.Invoke(this, approver);
                if (continuation != null) await continuation(approver);
                return;
            }

            var result = await _session.SwitchAsync(sellerId, Pin);
            if (result != SwitchResult.Ok)
            {
                // Task 19: a seller who has never set a PIN gets to create one on the
                // spot instead of being stuck on "PIN не задан". See BeginPinSetup.
                if (result == SwitchResult.PinNotSet)
                {
                    BeginPinSetup();
                    return;
                }

                Fail(MessageFor(result));
                return;
            }

            Succeed();
        }
        finally
        {
            _isBusy = false;
        }
    }

    // -----------------------------------------------------------------------------
    // PIN setup (Task 19): a seller whose pin_hash is empty gets to set their own
    // PIN on first use instead of requiring an administrator to seed it ahead of
    // time. Scoped to the plain switch flow only (see the PinNotSet branch in
    // SubmitAsync above) — never approval mode. An escalation approval keeps the
    // old "PIN не задан" error instead, for two reasons: (1) the whole point of
    // this flow is that the seller ends up selected as Current, which approval
    // never does by design, and (2) the offline degradation below relies on the
    // register falling back to crediting the shift owner when Current stays
    // unset — a guarantee that only holds for the switch flow.
    // -----------------------------------------------------------------------------

    /// <summary>Moves the overlay from "PIN rejected as not set" into the first step
    /// of PIN creation: same PIN pad, different prompt (<see cref="IsCreatingNewPin"/>).</summary>
    private void BeginPinSetup()
    {
        IsSettingPin = true;
        IsConfirmingNewPin = false;
        _pendingNewPin = null;
        Pin = string.Empty;
        HasError = false;
        ErrorMessage = string.Empty;
    }

    /// <summary>Handles one 4-digit entry while <see cref="IsSettingPin"/> is true —
    /// either the first entry, the confirming second entry (compared locally, no
    /// network yet), or — once both agree and pass <see cref="IsTrivialPin"/> —
    /// the actual <see cref="ISellerRosterService.SetPinAsync"/> round-trip.</summary>
    private async Task SubmitPinSetupStepAsync(string sellerId)
    {
        if (!IsConfirmingNewPin)
        {
            // First entry: reject the obviously-trivial patterns the backend also
            // rejects (see IsTrivialPin) right here, before asking for a second
            // entry that would only be wasted — an instant, specific message beats
            // a round-trip whose failure SetPinAsync's bool-only contract can't
            // even distinguish from "offline" (see the class-level PIN-setup
            // remarks and this decision's write-up in the task report).
            if (IsTrivialPin(Pin))
            {
                Pin = string.Empty;
                HasError = true;
                ErrorMessage = I18nService.Instance["PinTooWeak"];
                return;
            }

            _pendingNewPin = Pin;
            Pin = string.Empty;
            IsConfirmingNewPin = true;
            HasError = false;
            ErrorMessage = string.Empty;
            return;
        }

        if (Pin != _pendingNewPin)
        {
            // Restart the whole entry, not just the second attempt: a mismatch
            // means at least one of the two was a typo, and there is no way to
            // tell which — the first entry is no longer trustworthy either.
            Pin = string.Empty;
            _pendingNewPin = null;
            IsConfirmingNewPin = false;
            HasError = true;
            ErrorMessage = I18nService.Instance["PinMismatch"];
            return;
        }

        var confirmedPin = Pin;
        _pendingNewPin = null;

        // The actual network round-trip — the one genuine suspension point in this
        // whole flow, and exactly what _isBusy exists to cover (see the class-level
        // remarks): without it, a tap on Back or a fresh Open() while this is
        // in-flight could hide/reset the overlay, or start a second, overlapping
        // setup attempt, out from under this still-pending call.
        _isBusy = true;
        try
        {
            var success = await _rosterService.SetPinAsync(sellerId, confirmedPin);
            if (!success)
            {
                // Offline (or any other SetPinAsync failure — see its own remarks on
                // why it can't distinguish the two): close without selecting anyone.
                // The register's existing designed fallback (Pay() omitting SellerId
                // when Current is null) then credits the shift owner instead.
                CloseWithoutSelecting(I18nService.Instance["SellerPinSetupOffline"]);
                return;
            }

            // SetPinAsync already refreshed SellerRosterService's own cache, but
            // ISellerSession holds its own separate in-memory roster (see
            // ISellerSession.LoadRosterAsync) that knows nothing about that yet —
            // it must be handed the fresh hash explicitly before the PIN just set
            // can verify against it.
            await _session.LoadRosterAsync(await _rosterService.GetCachedAsync());

            var result = await _session.SwitchAsync(sellerId, confirmedPin);
            if (result != SwitchResult.Ok)
            {
                // Should not happen: the server just accepted this exact PIN and the
                // roster was reloaded with its hash. If it somehow still doesn't
                // verify (e.g. the seller vanished from the roster in that instant),
                // there is nothing productive to retry — degrade the same way the
                // offline branch above does rather than leaving the overlay stuck in
                // a half-finished PIN-setup state.
                Debug.Assert(false,
                    $"{nameof(SubmitPinSetupStepAsync)}: SwitchAsync unexpectedly returned {result} immediately after SetPinAsync succeeded.");
                CloseWithoutSelecting(MessageFor(result));
                return;
            }

            Succeed();
        }
        finally
        {
            _isBusy = false;
        }
    }

    /// <summary>The backend rejects PINs that are all one digit (e.g. "1111") or a
    /// run of four consecutive ascending/descending digits (e.g. "1234", "4321").
    /// The UI only ever collects exactly <see cref="PinLength"/> digits, so the
    /// backend's separate 4-6 length rule can never be violated here and isn't
    /// checked.</summary>
    private static bool IsTrivialPin(string pin)
    {
        if (pin.Length != PinLength) return false; // defensive; UI never allows this

        var allSame = true;
        var ascending = true;
        var descending = true;
        for (var i = 1; i < pin.Length; i++)
        {
            if (pin[i] != pin[0]) allSame = false;
            if (pin[i] - pin[i - 1] != 1) ascending = false;
            if (pin[i - 1] - pin[i] != 1) descending = false;
        }

        return allSame || ascending || descending;
    }

    /// <summary>Maps a failure <see cref="SwitchResult"/> (never <see cref="SwitchResult.Ok"/>,
    /// which both callers handle before reaching here) to a user-facing message. Shared by both
    /// modes: an escalation approval is a PIN check against the same roster with the same
    /// failure reasons as switching, so it deserves the same accurate wording — in particular
    /// <see cref="SwitchResult.CorruptHash"/> and <see cref="SwitchResult.UnknownSeller"/> must
    /// not say "wrong PIN", since retrying can never succeed for either until the roster
    /// refreshes.</summary>
    private static string MessageFor(SwitchResult result)
    {
        switch (result)
        {
            case SwitchResult.WrongPin: return I18nService.Instance["SellerPinWrong"];
            case SwitchResult.Locked: return I18nService.Instance["SellerLocked"];
            case SwitchResult.PinNotSet: return I18nService.Instance["SellerPinNotSet"];
            case SwitchResult.UnknownSeller: return I18nService.Instance["SellerNotOnRoster"];
            case SwitchResult.CorruptHash: return I18nService.Instance["SellerHashCorrupt"];
            default:
                // Ok never reaches here — both callers check for it first. A hit
                // here means a new SwitchResult member was added without a case
                // above. A thrown exception would crash the PIN overlay mid-sale
                // over a message-formatting gap, so — same trade-off as
                // AssertUiThread in SellerSession — this is loud in Debug/tests
                // (immediate assertion failure) but degrades to a generic
                // message instead of taking down the till in Release.
                Debug.Assert(false, $"{nameof(MessageFor)} has no case for {result}.");
                return I18nService.Instance["SellerVerificationFailed"];
        }
    }

    /// <summary>Hides the overlay and clears all of its per-attempt state — shared by
    /// every exit path that doesn't need to leave an error message showing (
    /// <see cref="Succeed"/> and <see cref="Cancel"/>; <see cref="CloseWithoutSelecting"/>
    /// needs the same fields cleared but an error message left up, so it keeps its own
    /// copy rather than folding into this).</summary>
    private void HideAndReset()
    {
        IsVisible = false;
        IsPinEntry = false;
        IsSettingPin = false;
        IsConfirmingNewPin = false;
        _pendingNewPin = null;
        SelectedSeller = null;
        Pin = string.Empty;
        HasError = false;
        ErrorMessage = string.Empty;
    }

    /// <summary>Closes the overlay on a successful PIN check, mirroring the care
    /// <see cref="Show"/>/<see cref="Back"/> already take on entry/exit — leaving
    /// <see cref="SelectedSeller"/>/<see cref="Pin"/>/<see cref="IsPinEntry"/> populated
    /// after success was harmless while every consumer gates on <see cref="IsVisible"/>,
    /// but inconsistent with the rest of the class.</summary>
    private void Succeed() => HideAndReset();

    private void Fail(string message)
    {
        Pin = string.Empty;
        HasError = true;
        ErrorMessage = message;
    }

    /// <summary>Closes the overlay entirely without selecting anyone — the PIN-setup
    /// flow's own failure exit (offline, or the near-impossible post-success mismatch;
    /// see SubmitPinSetupStepAsync). Deliberately not the same as <see cref="Fail"/>,
    /// which keeps the overlay open for a retry: there is nothing to retry here
    /// (SetPinAsync already ran), so leaving the PIN pad up would just invite another
    /// attempt against a request that already happened.</summary>
    private void CloseWithoutSelecting(string message)
    {
        IsVisible = false;
        IsPinEntry = false;
        IsSettingPin = false;
        IsConfirmingNewPin = false;
        _pendingNewPin = null;
        SelectedSeller = null;
        Pin = string.Empty;
        HasError = true;
        ErrorMessage = message;
    }
}
