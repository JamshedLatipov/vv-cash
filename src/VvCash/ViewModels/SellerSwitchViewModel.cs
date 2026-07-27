using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Models;
using VvCash.Services;

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
/// overlay closes on success.</summary>
public partial class SellerSwitchViewModel : ViewModelBase
{
    private const int PinLength = 4;

    private readonly ISellerSession _session;
    private bool _approvalMode;

    // Set for the duration of SubmitAsync and checked by every other entry point
    // that mutates overlay state (SelectSeller, Back, a fresh Show via Open/
    // OpenForApproval). Today SellerSession's Task-returning members complete
    // synchronously (see its class remarks), so the dispatcher never actually
    // pumps other input mid-submit and this guard never trips in practice. It
    // exists anyway for the day that stops being true — e.g. a roster refresh or
    // a server round-trip during SubmitAsync — so a tap on "Back" or a fresh
    // Open() can't land mid-submit and hide/reset the overlay (or, in approval
    // mode, raise Approved) out from under a user who has already navigated away.
    private bool _isBusy;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isPinEntry;
    [ObservableProperty] private SellerInfo? _selectedSeller;
    [ObservableProperty] private string _pin = string.Empty;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public ObservableCollection<SellerInfo> Sellers { get; } = new();

    /// <summary>Raised when an escalation PIN was accepted, carrying the approving seller.</summary>
    public event EventHandler<SellerInfo>? Approved;

    public SellerSwitchViewModel(ISellerSession session)
    {
        _session = session;
    }

    /// <summary>Opens the overlay to switch the current seller. Lists the whole roster.</summary>
    public void Open() => Show(_ => true, approvalMode: false);

    /// <summary>Opens the overlay to approve an operation the current seller lacks
    /// <paramref name="hasRight"/> for. Lists only sellers holding that right, and a
    /// successful PIN here never changes <see cref="ISellerSession.Current"/> — it
    /// raises <see cref="Approved"/> instead.</summary>
    public void OpenForApproval(Func<SellerInfo, bool> hasRight) => Show(hasRight, approvalMode: true);

    private void Show(Func<SellerInfo, bool> filter, bool approvalMode)
    {
        // A fresh Open()/OpenForApproval() while a submit is in flight would
        // reset SelectedSeller/Pin/Sellers out from under SubmitAsync's still-
        // pending continuation. Ignore it — the in-flight submit resolves the
        // overlay itself (hides on success, shows an error on failure) shortly;
        // the caller can open again afterwards if still needed.
        if (_isBusy) return;

        _approvalMode = approvalMode;

        Sellers.Clear();
        foreach (var seller in _session.Roster.Where(filter))
            Sellers.Add(seller);

        // Reset everything so a previous failed attempt (wrong PIN, corrupt hash,
        // etc.) never leaks into the next time the overlay opens.
        SelectedSeller = null;
        Pin = string.Empty;
        HasError = false;
        ErrorMessage = string.Empty;
        IsPinEntry = false;
        IsVisible = true;
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
    }

    private async Task SubmitAsync()
    {
        if (SelectedSeller == null) return;
        var sellerId = SelectedSeller.Id;

        _isBusy = true;
        try
        {
            if (_approvalMode)
            {
                // ApproveAsync now surfaces the same SwitchResult vocabulary as
                // SwitchAsync (see ApprovalResult), so both modes share one
                // result-to-message mapping instead of the approval path
                // collapsing every failure into a generic "wrong PIN".
                var approval = await _session.ApproveAsync(sellerId, Pin);
                if (approval.Result != SwitchResult.Ok)
                {
                    Fail(MessageFor(approval.Result));
                    return;
                }

                // Result == Ok guarantees Approver is set — see ApprovalResult.Success.
                var approver = approval.Approver!;
                Succeed();
                Approved?.Invoke(this, approver);
                return;
            }

            var result = await _session.SwitchAsync(sellerId, Pin);
            if (result != SwitchResult.Ok)
            {
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

    /// <summary>Closes the overlay and resets all of its state on a successful PIN
    /// check, mirroring the care <see cref="Show"/>/<see cref="Back"/> already take
    /// on entry/exit — leaving <see cref="SelectedSeller"/>/<see cref="Pin"/>/
    /// <see cref="IsPinEntry"/> populated after success was harmless while every
    /// consumer gates on <see cref="IsVisible"/>, but inconsistent with the rest
    /// of the class.</summary>
    private void Succeed()
    {
        IsVisible = false;
        IsPinEntry = false;
        SelectedSeller = null;
        Pin = string.Empty;
        HasError = false;
        ErrorMessage = string.Empty;
    }

    private void Fail(string message)
    {
        Pin = string.Empty;
        HasError = true;
        ErrorMessage = message;
    }
}
