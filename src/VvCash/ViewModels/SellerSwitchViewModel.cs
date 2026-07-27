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
            var approver = await _session.ApproveAsync(sellerId, Pin);
            if (approver == null)
            {
                // ISellerSession.ApproveAsync deliberately collapses every failure
                // reason (wrong PIN, lockout, no PIN set, unknown seller, corrupt
                // hash) into a single null — unlike SwitchAsync it does not surface
                // a SwitchResult. "Wrong PIN" is the most common cause and the best
                // generic message available without changing that interface.
                Fail(I18nService.Instance["SellerPinWrong"]);
                return;
            }

            IsVisible = false;
            Approved?.Invoke(this, approver);
            return;
        }

        var result = await _session.SwitchAsync(sellerId, Pin);
        switch (result)
        {
            case SwitchResult.Ok:
                IsVisible = false;
                break;

            case SwitchResult.WrongPin:
                Fail(I18nService.Instance["SellerPinWrong"]);
                break;

            case SwitchResult.Locked:
                Fail(I18nService.Instance["SellerLocked"]);
                break;

            case SwitchResult.PinNotSet:
                Fail(I18nService.Instance["SellerPinNotSet"]);
                break;

            case SwitchResult.UnknownSeller:
                // The tapped tile came from this VM's own Sellers snapshot, taken
                // when the overlay opened, so this only fires if the roster was
                // reloaded out from under an open overlay (e.g. a background sync
                // removed this seller) between the tap and the fourth digit.
                // "Wrong PIN" would be just as misleading here as it would for
                // CorruptHash below: no PIN for this id can succeed until the
                // roster is reloaded.
                Fail(I18nService.Instance["SellerNotOnRoster"]);
                break;

            case SwitchResult.CorruptHash:
                // The cached hash itself is unusable — no PIN, not even the right
                // one, can ever succeed until the roster refreshes. Reusing the
                // wrong-PIN message would send the cashier into a pointless retry
                // loop, so this gets its own.
                Fail(I18nService.Instance["SellerHashCorrupt"]);
                break;
        }
    }

    private void Fail(string message)
    {
        Pin = string.Empty;
        HasError = true;
        ErrorMessage = message;
    }
}
