using System;
using System.Collections.ObjectModel;
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
        SelectedSeller = seller;
        Pin = string.Empty;
        HasError = false;
        ErrorMessage = string.Empty;
        IsPinEntry = true;
    }

    [RelayCommand]
    private async Task AppendDigitAsync(string digit)
    {
        // The >= guard (not ==) is what actually prevents a double submit if a
        // second tap is ever dispatched after the PIN is already full — see the
        // class remarks on SellerSession for why that second call, if it happens
        // at all, only ever arrives after the first one already ran to completion.
        if (SelectedSeller == null || Pin.Length >= PinLength) return;

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

            IsVisible = false;
            // Result == Ok guarantees Approver is set — see ApprovalResult.Success.
            Approved?.Invoke(this, approval.Approver!);
            return;
        }

        var result = await _session.SwitchAsync(sellerId, Pin);
        if (result != SwitchResult.Ok)
        {
            Fail(MessageFor(result));
            return;
        }

        IsVisible = false;
    }

    /// <summary>Maps a failure <see cref="SwitchResult"/> (never <see cref="SwitchResult.Ok"/>,
    /// which both callers handle before reaching here) to a user-facing message. Shared by both
    /// modes: an escalation approval is a PIN check against the same roster with the same
    /// failure reasons as switching, so it deserves the same accurate wording — in particular
    /// <see cref="SwitchResult.CorruptHash"/> and <see cref="SwitchResult.UnknownSeller"/> must
    /// not say "wrong PIN", since retrying can never succeed for either until the roster
    /// refreshes.</summary>
    private static string MessageFor(SwitchResult result) => result switch
    {
        SwitchResult.WrongPin => I18nService.Instance["SellerPinWrong"],
        SwitchResult.Locked => I18nService.Instance["SellerLocked"],
        SwitchResult.PinNotSet => I18nService.Instance["SellerPinNotSet"],
        SwitchResult.UnknownSeller => I18nService.Instance["SellerNotOnRoster"],
        SwitchResult.CorruptHash => I18nService.Instance["SellerHashCorrupt"],
        _ => throw new ArgumentOutOfRangeException(nameof(result), result,
            $"{nameof(MessageFor)} is only for failure results, not {SwitchResult.Ok}.")
    };

    private void Fail(string message)
    {
        Pin = string.Empty;
        HasError = true;
        ErrorMessage = message;
    }
}
