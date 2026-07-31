using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Constants;
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using VvCash.Services.Data;
using VvCash.Services.Discounts;
using VvCash.Services.Hardware;

namespace VvCash.ViewModels;

/// <summary>Payload for <see cref="PosViewModel.SellerSwitchRequested"/>. Exists because
/// not every raise site may offer the seller-switch overlay's manual sign-out control
/// (<see cref="SellerSwitchViewModel.CanSignOut"/>): <see cref="PosViewModel.AddToCart"/>
/// and <see cref="PosViewModel.ResumeParkedSale"/> both raise this event while the cart is
/// still empty <em>by construction</em> — that is exactly the condition their own gates
/// check — but only because they are about to fill it one statement/moment later. Reading
/// <see cref="PosViewModel.CanEndSellerSession"/> at the raise site in either case would
/// read true and be a lie by the time the overlay is actually on screen: the cashier would
/// see the sign-out button over a cart that, by then, has an item — the exact bug the
/// critical fix in the 2026-07-31 design doc's addendum closes. Only a raise site that is
/// not itself about to add to the cart — today, just the manual chip tap
/// (<see cref="PosViewModel.OpenSellerSwitch"/>) — may set <see cref="CanSignOut"/> true.</summary>
public sealed class SellerSwitchRequest : EventArgs
{
    public bool CanSignOut { get; }

    public SellerSwitchRequest(bool canSignOut) => CanSignOut = canSignOut;
}

public partial class PosViewModel : ViewModelBase, IDisposable
{
    private readonly IProductService _productService;
    private readonly ICategoryService _categoryService;
    private readonly ICartService _cartService;
    private readonly IPrinterService _printerService;
    private readonly ICustomerDisplayService _customerDisplayService;
    private readonly IShiftService _shiftService;
    private readonly IOfflineStorageService _offlineStorageService;
    private readonly ISyncService _syncService;
    private readonly ISettingsService _settingsService;
    private readonly IExpenseDocumentService _expenseDocumentService;
    private readonly ICounterpartyService _counterpartyService;
    private readonly IParkedSaleService _parkedSaleService;
    private readonly IReturnService _returnService;
    private readonly ICashOperationService _cashOperationService;
    private readonly IQuoteService _quoteService;
    private readonly IPromotionProvider _promotionProvider;
    private readonly ISessionContext _session;
    private readonly HttpClient _httpClient;
    private readonly ISellerSession _sellerSession;
    private readonly ISellerRosterService _rosterService;
    private readonly IAuthService _authService;
    private readonly ICashFeatureService _features;
    private CancellationTokenSource? _syncCancellationTokenSource;
    private System.Threading.CancellationTokenSource? _quoteCts;
    private bool _applyingQuoteResult;
    private string? _activePromoCode;

    /// <summary>Id of the seller who approved the current receipt's manual discount when
    /// it exceeded the ringing seller's own cap (see <see cref="NeedsDiscountApproval"/>
    /// / <see cref="ApplyApprovedDiscount"/>) — stamped onto <see cref="DocumentRequest.ApprovedBy"/>
    /// in <see cref="Pay"/>. Lives only for the current receipt: cleared everywhere the
    /// cart itself is cleared (<see cref="ClearCart"/>, Pay's own success branch) and
    /// whenever the manual discount is replaced or removed without a fresh approval
    /// (<see cref="ApplyManualDiscount"/>, <see cref="ClearManualDiscount"/>), so it can
    /// never leak into a receipt — or a discount — it wasn't actually approved for.
    /// Parking is the one exception to "cleared": the approval genuinely happened, so
    /// <see cref="BuildSnapshot"/> carries this value into
    /// <see cref="ParkedSaleSnapshot.ApprovedById"/> before the field is reset for the
    /// next receipt, and <see cref="ResumeParkedSale"/> restores it — re-prompting a
    /// supervisor to re-approve their own earlier decision would be wrong, and would fail
    /// outright once that supervisor has gone home.</summary>
    private string? _approvedById;

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private ObservableCollection<Product> _products = new();
    [ObservableProperty] private ObservableCollection<CartItem> _cartItems = new();
    [ObservableProperty] private ObservableCollection<Category> _allCategories = new();
    [ObservableProperty] private ObservableCollection<Category> _quickCategories = new();
    [ObservableProperty] private ObservableCollection<Category> _currentDisplayedCategories = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCategoryName))]
    [NotifyPropertyChangedFor(nameof(CanNavigateUp))]
    private Category? _currentParentCategory;
    private readonly Stack<Category?> _categoryNavStack = new();
    public bool CanNavigateUp => _categoryNavStack.Count > 0;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCategoryName))]
    private Category? _selectedCategory;
    [ObservableProperty] private bool _hasSubcategories;
    public string SelectedCategoryName => CurrentParentCategory?.Name ?? SelectedCategory?.Name ?? "All Categories";
    [ObservableProperty] private bool _isViewingCategories = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsyncedDocuments))]
    private int _unsyncedDocumentsCount;

    public bool HasUnsyncedDocuments => UnsyncedDocumentsCount > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasParkedSales))]
    [NotifyPropertyChangedFor(nameof(IsParkedSalesListVisible))]
    private int _parkedSalesCount;
    public bool HasParkedSales => ParkedSalesCount > 0;

    /// <summary>Feature-flag visibility for the POS screen. Snapshotted by
    /// <see cref="ApplyFeatures"/> — see its own remarks for why these are read once per
    /// screen rather than kept live.</summary>
    [ObservableProperty] private bool _isReturnsEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsParkedSalesListVisible))]
    private bool _isParkingEnabled = true;

    [ObservableProperty] private bool _isMixedPaymentEnabled = true;
    [ObservableProperty] private bool _isCustomerRegistrationEnabled = true;
    [ObservableProperty] private bool _isSellerSwitchEnabled = true;
    [ObservableProperty] private bool _isCustomerDisplayEnabled = true;
    [ObservableProperty] private bool _isDiscountEnabled = true;
    [ObservableProperty] private bool _isCouponsEnabled = true;

    /// <summary>Exchange is hidden when the store switched the function off, and
    /// disabled while the register is offline: an exchange cannot be queued, so
    /// offering the button without a connection would promise something the
    /// register cannot deliver. Read live from the flag map rather than snapshotted
    /// into a field like the flags above, so <see cref="IsSystemOnline"/> flipping
    /// updates it on its own — which also means ApplyFeatures has to raise both
    /// explicitly when the map lands, since no generated setter will.</summary>
    public bool IsExchangeVisible => _features.Current.IsEnabled(CashFeatureCodes.Exchange);
    public bool IsExchangeEnabled => IsExchangeVisible && IsSystemOnline;

    /// <summary>A display that was fed cart data before the flag actually loaded (see
    /// ApplyFeatures' remarks: it runs once synchronously with the default cache, then
    /// again once InitializeAsync's real fetch resolves) must not keep showing that
    /// stale cart once the flag turns out to be off — otherwise a customer-facing screen
    /// the store just disabled would sit there displaying someone else's total. Reset to
    /// idle exactly once, on the off transition; the guarded push sites (OnCartChanged,
    /// the payment-success branch) take over from there and simply stop pushing.</summary>
    partial void OnIsCustomerDisplayEnabledChanged(bool value)
    {
        if (!value && CustomerDisplayViewModel != null)
            CustomerDisplayViewModel.IsIdle = true;
    }

    /// <summary>Parked sales already on this register outlive the flag being
    /// switched off: "Park" disappears at once, but the list stays reachable
    /// until the last one is cleared. Otherwise switching the flag off would
    /// strand receipts with goods picked and no money taken.</summary>
    public bool IsParkedSalesListVisible => IsParkingEnabled || ParkedSalesCount > 0;

    // Park label modal
    [ObservableProperty] private bool _isParkLabelModalVisible = false;
    [ObservableProperty] private string _parkLabelInput = string.Empty;

    // Shift-close confirmation (when parked sales exist)
    [ObservableProperty] private bool _isShiftCloseConfirmVisible = false;

    [ObservableProperty] private string _couponCode = string.Empty;
    [ObservableProperty] private ObservableCollection<Coupon> _appliedCoupons = new();
    [ObservableProperty] private decimal _subtotal;

    // Coupon modal (coupons live in a modal instead of the totals panel)
    [ObservableProperty] private bool _isCouponModalVisible = false;

    public bool HasAppliedCoupons => AppliedCoupons.Count > 0;
    partial void OnAppliedCouponsChanged(ObservableCollection<Coupon> value)
        => OnPropertyChanged(nameof(HasAppliedCoupons));

    // Cart summary helpers for the always-visible order panel
    // Decimal: a weighted line contributes a fraction of a unit to the count.
    public decimal CartItemsCount => CartItems.Sum(i => i.Quantity);
    public bool HasCartItems => CartItems.Count > 0;
    partial void OnCartItemsChanged(ObservableCollection<CartItem> value)
    {
        OnPropertyChanged(nameof(CartItemsCount));
        OnPropertyChanged(nameof(HasCartItems));
    }

    public bool HasTotalDiscount => TotalDiscount > 0;
    partial void OnTotalDiscountChanged(decimal value)
        => OnPropertyChanged(nameof(HasTotalDiscount));

    /// <summary>Name of the discount source in force — the promotion, the promo
    /// code, the card. Empty when the discount is purely the cashier's manual one.</summary>
    [ObservableProperty] private string _appliedDiscountName = string.Empty;
    public bool HasAppliedDiscountName => !string.IsNullOrWhiteSpace(AppliedDiscountName);
    partial void OnAppliedDiscountNameChanged(string value)
        => OnPropertyChanged(nameof(HasAppliedDiscountName));

    public bool HasProducts => Products.Count > 0;
    public bool ShowCatalogEmptyState => !IsViewingCategories && !HasProducts;
    partial void OnProductsChanged(ObservableCollection<Product> value)
    {
        OnPropertyChanged(nameof(HasProducts));
        OnPropertyChanged(nameof(ShowCatalogEmptyState));
    }
    partial void OnIsViewingCategoriesChanged(bool value)
        => OnPropertyChanged(nameof(ShowCatalogEmptyState));

    // Quantity pad — the only place a line's exact amount can be entered, and
    // the only place a secondary unit can be chosen.
    [ObservableProperty] private bool _isQuantityPadVisible = false;
    [ObservableProperty] private QuantityPadViewModel? _quantityPad;

    // Manual Discount Properties
    [ObservableProperty] private bool _isDiscountModalVisible = false;
    [ObservableProperty] private string _discountInputValue = string.Empty;
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsDiscountAmountMode))]
    private bool _isDiscountPercentMode = true;

    public bool IsDiscountAmountMode
    {
        get => !IsDiscountPercentMode;
        set => IsDiscountPercentMode = !value;
    }

    [ObservableProperty] private decimal _manualDiscountAmount;

    public bool HasManualDiscount => ManualDiscountAmount > 0;
    partial void OnManualDiscountAmountChanged(decimal value)
        => OnPropertyChanged(nameof(HasManualDiscount));

    // Selected customer
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCustomer))]
    [NotifyPropertyChangedFor(nameof(SelectedCustomerName))]
    [NotifyPropertyChangedFor(nameof(SelectedCustomerDiscount))]
    [NotifyPropertyChangedFor(nameof(HasCustomerDiscount))]
    [NotifyPropertyChangedFor(nameof(CustomerDiscountAmount))]
    private VvCash.Models.Api.CounterpartyResponse? _selectedCustomer;

    public bool HasSelectedCustomer => SelectedCustomer != null;
    public string SelectedCustomerName => SelectedCustomer?.FullName ?? string.Empty;
    public decimal SelectedCustomerDiscount => SelectedCustomer?.DiscountCard?.Discount ?? 0m;
    public bool HasCustomerDiscount => SelectedCustomer?.DiscountCard != null && SelectedCustomer.DiscountCard.Discount > 0;
    public decimal CustomerDiscountAmount => SelectedCustomerDiscount / 100m * Subtotal;

    [RelayCommand]
    private void ClearSelectedCustomer()
    {
        SelectedCustomer = null;
        _cartService.ClearCustomerDiscount();
        TriggerRequote();
    }

    [ObservableProperty] private decimal _totalDiscount;
    [ObservableProperty] private decimal _totalAmount;
    [ObservableProperty] private string _printerStatusText = "Printer Ready";
    [ObservableProperty] private bool _isPrinterReady = true;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isShiftOpen = false;
    [ObservableProperty] private bool _isShiftModalVisible = false;
    [ObservableProperty] private bool _isLoadingShift = false;
    [ObservableProperty] private string? _currentShiftId;
    [ObservableProperty] private int _orderNumber = 1;
    [ObservableProperty] private string _orderDateTime = string.Empty;
    [ObservableProperty] private bool _isAlertModalVisible = false;
    [ObservableProperty] private string _alertMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SystemStatusText))]
    [NotifyPropertyChangedFor(nameof(IsExchangeEnabled))]
    private bool _isSystemOnline = true;

    public string SystemStatusText => IsSystemOnline ? "SYSTEM ONLINE" : "SYSTEM OFFLINE";

    /// <summary>Task 22: set (on the UI thread — see <see cref="OnSessionRevoked"/>) once
    /// <see cref="IExpenseDocumentService.SessionRevoked"/> fires, driving the banner in
    /// PosView.axaml. Deliberately never cleared back to false by anything in this class:
    /// the server rejecting the shift session (401) means the current auth token is bad,
    /// and a bad token doesn't heal itself — the next queued document hits the exact same
    /// 401 and SyncOfflineDocumentsAsync stops at the first one every time (see its own
    /// remarks), so no sync can ever succeed afterward to justify auto-clearing the banner.
    /// Silently hiding it on some later event the cashier didn't cause would be actively
    /// misleading: the only thing that actually fixes a revoked session is signing in
    /// again, and that flow constructs a brand-new PosViewModel (see Dispose's own remarks
    /// on why this class is transient across logout/login), so the banner's true reset is
    /// simply this instance going away.</summary>
    [ObservableProperty] private bool _isSessionRevoked;

    /// <summary>Raised to ask the host (App.axaml.cs) to return to the login screen — the
    /// escape hatch this class otherwise has no way to reach on its own, since the
    /// LoginViewModel instance the host needs to navigate to (with its LoginSuccessful
    /// handler already wired at startup — see App.axaml.cs's NavigateToPos) was never handed
    /// to PosViewModel. Same decoupling role as SellerSwitchRequested and friends above:
    /// raise intent, let the host do the mechanics. Fired by two callers that converge on the
    /// same underlying <see cref="PerformSignOut"/> — <see cref="OnShiftSessionRevoked"/>
    /// (the server rejected the session) and <see cref="SignOut"/> (the shift modal's manual
    /// escape hatch) — so the two can never drift apart. The string argument is an
    /// already-localized explanation to show on the login screen, or empty for a plain,
    /// cashier-initiated sign-out with nothing to explain.</summary>
    public event EventHandler<string>? LogoutRequested;

    public CustomerDisplayViewModel? CustomerDisplayViewModel { get; set; }
    public Action<ViewModelBase>? NavigationRequest { get; set; }
    public IAsyncRelayCommand OpenCustomerRegistrationCommand { get; }
    public IRelayCommand CloseApplicationCommand { get; }

    /// <summary>Set by App.axaml.cs (mirrors <see cref="CustomerDisplayViewModel"/>): the
    /// overlay view model PosView hosts and binds its DataContext to. PosViewModel doesn't
    /// own its lifecycle, only asks for it to open via <see cref="SellerSwitchRequested"/>.</summary>
    public SellerSwitchViewModel? SellerSwitchViewModel { get; set; }

    /// <summary>Raised to ask the host (App.axaml.cs) to open the seller-switch overlay —
    /// either because the register requires a fresh seller confirmation at the start of a
    /// receipt (see <see cref="AddToCart"/>, <see cref="ResumeParkedSale"/>) or because the
    /// cashier tapped the seller chip (see <see cref="OpenSellerSwitch"/>). Plays the same
    /// decoupling role as <see cref="NavigationRequest"/> and
    /// <see cref="CustomerDisplayViewModel"/> — PosViewModel raises intent without knowing
    /// how it's fulfilled — but unlike those two settable delegate/property members, this
    /// is a genuine event: the host subscribes to it rather than being handed a callback to
    /// invoke. Carries a <see cref="SellerSwitchRequest"/> — see its own remarks for why the
    /// permission it carries must be computed by the raise site itself, at the moment it
    /// raises, rather than read later by the host.</summary>
    public event EventHandler<SellerSwitchRequest>? SellerSwitchRequested;

    /// <summary>Raised to ask the host (App.axaml.cs) to open the seller-switch
    /// overlay in approval mode (see <see cref="SellerSwitchViewModel.OpenForApproval"/>)
    /// because the current seller lacks <c>CanCloseShift</c> — see <see cref="CloseShift"/>.
    /// Plays the same decoupling role as <see cref="SellerSwitchRequested"/>: PosViewModel
    /// raises intent without knowing how the overlay gets opened.</summary>
    public event EventHandler? CloseShiftApprovalRequested;

    /// <summary>Raised to ask the host (App.axaml.cs) to open the seller-switch overlay in
    /// approval mode because the current seller lacks <c>CanRefund</c> — see
    /// <see cref="OpenReturns"/>. Same role as <see cref="CloseShiftApprovalRequested"/>.</summary>
    public event EventHandler? RefundApprovalRequested;

    /// <summary>Raised to ask the host (App.axaml.cs) to open the seller-switch overlay in
    /// approval mode because a manual discount (see <see cref="ApplyManualDiscount"/>)
    /// exceeds the current seller's <c>MaxDiscount</c> cap — see
    /// <see cref="NeedsDiscountApproval"/> for exactly when. Carries the requested percent
    /// so the host can both filter approvers by it (only a seller whose own cap covers
    /// this percent may approve) and hand the same value back to
    /// <see cref="ApplyApprovedDiscount"/> once approved.</summary>
    public event EventHandler<decimal>? DiscountApprovalRequested;

    /// <summary>Current seller's name for the header chip, or — when none is selected — the
    /// same action-shaped invitation ("Who is selling?") already used by this button's
    /// tooltip and by the overlay's own heading, so an empty chip reads as something to
    /// press rather than as a caption. Recomputed whenever
    /// <see cref="ISellerSession.CurrentChanged"/> fires (see <see cref="OnSellerChanged"/>).</summary>
    public string SellerChipText => _sellerSession.Current?.FullName ?? I18nService.Instance["SelectSeller"];

    private void OnSellerChanged(object? sender, EventArgs e)
        => OnPropertyChanged(nameof(SellerChipText));

    [RelayCommand]
    private void OpenSellerSwitch()
    {
        // Disabled entry points are hidden, not greyed out (see PosView.axaml's binding
        // on this command's button) — but the command itself must also refuse, since a
        // stray click on a control mid-hide-animation, or any other path that still
        // reaches this method, must not open an overlay this register's flag says
        // doesn't apply here.
        if (!IsSellerSwitchEnabled) return;

        // The one raise site allowed to grant sign-out (see SellerSwitchRequest's own
        // remarks): tapping the chip does not itself add anything to the cart, unlike
        // AddToCart/ResumeParkedSale below. CanEndSellerSession read right now is accurate
        // for this instant — it is not a guarantee that the cart stays empty for as long
        // as the overlay ends up showing: HandleBarcodeAsync awaits a product lookup
        // before posting AddToCart, so a scan already in flight before this tap could
        // still land afterwards. PosView.axaml.cs's keyboard guard (see
        // IsSellerSwitchOverlayVisible) is what closes the direct route of a scan
        // reaching the cart while the overlay is up; this comment is only about what this
        // one read of CanEndSellerSession itself promises.
        SellerSwitchRequested?.Invoke(this, new SellerSwitchRequest(CanEndSellerSession));
    }

    [RelayCommand]
    private async Task OpenShiftAsync()
    {
        Console.WriteLine("[PosViewModel] OpenShiftAsync command executed.");
        System.Diagnostics.Debug.WriteLine("[PosViewModel] OpenShiftAsync command executed.");
        IsLoadingShift = true;
        CurrentShiftId = await _shiftService.OpenShiftAsync();
        IsLoadingShift = false;
        if (!string.IsNullOrEmpty(CurrentShiftId))
        {
            IsShiftOpen = true;
            IsShiftModalVisible = false;

            // This can land moments after (or overlap with) StartBackgroundSync's own
            // roster refresh on startup (see InitializeAsync below and the periodic
            // loop) — that's fine, not a duplicate-fetch bug: SellerRosterService
            // coalesces overlapping RefreshAsync callers onto a single in-flight fetch,
            // so both call sites end up with the identical result instead of racing two
            // independent HTTP round-trips where a stale one could resolve last and
            // overwrite a fresh one. Kept deliberately per Task 17's spec (load on
            // shift open) rather than removed to avoid the overlap.
            //
            // OpenShiftAsync is a [RelayCommand], invoked from a UI-triggered Execute()
            // and — since this codebase never uses ConfigureAwait(false) — every await
            // above resumes back on the UI thread via the captured Avalonia
            // SynchronizationContext, so we're still on the UI thread here.
            // LoadRosterAsync mutates SellerSession state and requires that; no extra
            // Dispatcher marshalling is needed.
            await _sellerSession.LoadRosterAsync(await _rosterService.RefreshAsync());
        }
    }

    [RelayCommand]
    private async Task CloseShift()
    {
        if (string.IsNullOrEmpty(CurrentShiftId)) return;

        // Closing a shift requires CanCloseShift. Nobody having confirmed at all
        // (Current == null) is treated the same as lacking the right — fail closed,
        // same reasoning as SellerSession.LoadRosterAsync treating a disabled
        // seller as absent. A seller who lacks it must escalate through a
        // supervisor PIN instead of closing outright: raise intent (mirrors
        // AddToCart's SellerSwitchRequested) and let the host open the overlay.
        //
        // With seller switching disabled this register has no notion of separate
        // sellers: everything is the shift owner's, and their rights are the only
        // rights. Leaving the gate on while the approval overlay is hidden would
        // make the shift impossible to close — nobody ever becomes Current, so the
        // gate would fire forever with nothing able to satisfy it.
        if (IsSellerSwitchEnabled && !(_sellerSession.Current?.CanCloseShift ?? false))
        {
            CloseShiftApprovalRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        await ProceedToCloseShiftAsync();
    }

    private async Task ProceedToCloseShiftAsync()
    {
        if (ParkedSalesCount > 0)
        {
            IsShiftCloseConfirmVisible = true;
            return;
        }

        await DoCloseShiftAsync();
    }

    /// <summary>The continuation App.axaml.cs hands to
    /// <c>SellerSwitchViewModel.OpenForApproval</c> when wiring up
    /// <see cref="CloseShiftApprovalRequested"/>. SellerSwitchViewModel invokes this only
    /// when the specific approval it was opened for succeeds (each <c>OpenForApproval</c>
    /// call owns its own continuation slot — see that class's remarks), so unlike the
    /// pending-flag this used to need, a cancelled or unrelated approval can never reach
    /// here: without a real completion there is nothing for App.axaml.cs to have wired.
    /// This is what makes the approval flow actually finish closing the shift rather than
    /// just dismissing the overlay.</summary>
    public async Task OnCloseShiftApproved()
    {
        await ProceedToCloseShiftAsync();
    }

    [RelayCommand]
    private async Task ConfirmCloseShift()
    {
        IsShiftCloseConfirmVisible = false;
        await DoCloseShiftAsync();
    }

    [RelayCommand]
    private void CancelCloseShift()
    {
        IsShiftCloseConfirmVisible = false;
    }

    private async Task DoCloseShiftAsync()
    {
        Console.WriteLine("[PosViewModel] DoCloseShiftAsync executed.");
        System.Diagnostics.Debug.WriteLine("[PosViewModel] DoCloseShiftAsync executed.");
        if (string.IsNullOrEmpty(CurrentShiftId)) return;

        IsLoadingShift = true;
        bool success = await _shiftService.CloseShiftAsync(CurrentShiftId);
        IsLoadingShift = false;
        if (success)
        {
            CurrentShiftId = null;
            IsShiftOpen = false;
            IsShiftModalVisible = true;

            // The auth token's lifetime is now tied to the shift (see
            // AuthConstants.MaxShiftHours), so a closed shift must not leave a
            // still-"remembered" token sitting in settings for the next register
            // start to silently resume from, nor a stale seller left selected
            // against a session that — as far as the register is concerned — just
            // ended. Deliberately only on this success branch: a cancelled confirm
            // dialog or a failed CloseShiftAsync call must leave both untouched, so
            // the shift (and whoever is signed in) is genuinely still open.
            //
            // Wiping AuthToken/AuthTokenExpiresAt is IAuthService's job, not this
            // view model's — AuthService.LoginAsync is the only other writer of those
            // fields, and duplicating that logic here (reaching into ISettingsService
            // directly) would let the two drift apart.
            _sellerSession.Clear();
            _authService.ClearSession();
        }
    }

    [RelayCommand]
    private void CloseAlertModal()
    {
        IsAlertModalVisible = false;
    }

    private void CloseApplication()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow?.Close();
        }
    }

    /// <summary>The shift modal's manual escape hatch (see PosView.axaml's Start Shift Modal
    /// Overlay). Deliberately available whenever the modal is up regardless of *why* — a dead
    /// remembered session (see <see cref="OnShiftSessionRevoked"/>), a shift closed without
    /// ever navigating away (DoCloseShiftAsync above clears the token but stays on this same
    /// view), or simply the wrong cashier having opened the app — the fix is the same in every
    /// case: sign out and let the next person log in properly instead of being trapped behind
    /// a modal that can never succeed with no valid session.</summary>
    [RelayCommand]
    private void SignOut() => PerformSignOut(string.Empty);

    /// <summary>Single choke point for leaving the current session — shared by the automatic
    /// 401 recovery (<see cref="OnShiftSessionRevoked"/>) and the manual escape hatch
    /// (<see cref="SignOut"/>) so the two can never diverge. Mirrors DoCloseShiftAsync's own
    /// sign-out branch (clear seller + auth token) but also asks the host to navigate away,
    /// which a mid-shift close deliberately does not do. Does not touch the cart or any
    /// queued offline document: clearing the auth token doesn't discard anything already
    /// saved via IOfflineStorageService, and ExpenseDocumentService.SyncOfflineDocumentsAsync
    /// picks the queue back up on its own once a valid session exists again — see this
    /// class's own remarks on why IsSessionRevoked is never cleared for the same reason.</summary>
    private void PerformSignOut(string explanation)
    {
        _sellerSession.Clear();
        _authService.ClearSession();
        LogoutRequested?.Invoke(this, explanation);
    }

    /// <summary>True when the cart has no items right now — the exact condition
    /// <see cref="EndReceipt"/>'s own guard checks below, exposed here for the manual
    /// counterpart to that automatic reset: the seller-switch overlay's "stop selling"
    /// control (see <see cref="SellerSwitchViewModel.CanSignOut"/>) must never be offered
    /// mid-receipt, for the same reason EndReceipt itself refuses to fire then —
    /// AddToCart's gate only re-asks who is selling on an EMPTY cart, so dropping the
    /// seller with items still in the cart would leave the rest of that receipt with
    /// nobody confirmed and nothing to re-prompt.
    ///
    /// A momentary snapshot, not a durable guarantee: only <see cref="OpenSellerSwitch"/>
    /// (the manual chip tap, which adds nothing to the cart itself) may read this and pass
    /// it through <see cref="SellerSwitchRequest.CanSignOut"/> — <see cref="AddToCart"/>
    /// and <see cref="ResumeParkedSale"/> both observe the cart empty for the same instant
    /// this property would report true, but only because they are each about to fill it;
    /// see <see cref="SellerSwitchRequest"/>'s remarks for the bug that reading this at
    /// those raise sites caused. PosViewModel still has no reason to know
    /// SellerSwitchViewModel exists — App.axaml.cs forwards whatever value the raising
    /// method already decided, via <see cref="SellerSwitchRequest.CanSignOut"/>, rather
    /// than reading this property itself.</summary>
    public bool CanEndSellerSession => !_cartService.Items.Any();

    /// <summary>Every finished operation is meant to funnel through here — a successful
    /// payment, the cashier manually clearing the receipt, and a returns/exchange dialog
    /// that genuinely booked a document — to say "nobody is confirmed any more". The idle
    /// timeout stays as a second line of defence for a receipt abandoned halfway; this
    /// one closes the window where the next person starts ringing up within 90 seconds
    /// and their sale is silently credited to whoever sold last (see the 2026-07-31 spec).
    ///
    /// Kept as one method rather than four inline Clear() calls for the same reason
    /// PerformSignOut above is one method: the next end-of-receipt path added to this
    /// class must have one obvious place to hook into, or it will quietly skip the reset.
    ///
    /// No IsSellerSwitchEnabled guard on purpose: with switching off nobody ever becomes
    /// Current, and SellerSession.Clear() returns early when Current is already null, so
    /// this degrades to a no-op on its own.
    ///
    /// Guarded on an empty cart: the returns/exchange dialogs are separate windows that
    /// never touch the POS cart, so a dialog can close having booked a document while the
    /// current receipt is still mid-ring — clearing here would leave the rest of that
    /// receipt with nobody confirmed and AddToCart's gate re-asking only on an empty cart,
    /// so nothing would ever re-prompt. Pay and ClearCart both empty the cart themselves
    /// before calling this, so the guard is a no-op for them.</summary>
    private void EndReceipt()
    {
        // Never mid-receipt: a returns/exchange dialog can be opened over a cart that is
        // still being rung up, and AddToCart's gate only re-asks on an EMPTY cart — so
        // clearing here would leave the rest of that receipt with nobody confirmed and
        // nothing to prompt. Pay and ClearCart both empty the cart before calling this,
        // so the guard is a no-op for them.
        if (_cartService.Items.Any()) return;
        _sellerSession.Clear();
    }

    [RelayCommand]
    private async Task FullReinitializeAsync()
    {
        StatusMessage = "Starting full database reinitialization...";
        await _syncService.FullReinitializeAsync();
        await LoadCategoriesAsync();
        await LoadProductsAsync(SelectedCategory?.Id);
        // Surface the catalog size so an empty/failed sync is immediately visible.
        var totalProducts = (await _productService.GetAllProductsAsync()).Count();
        StatusMessage = totalProducts > 0
            ? $"Catalog updated: {totalProducts} products, {AllCategories.Count} categories."
            : "Sync finished but catalog is EMPTY — check Backend URL / tokens in Settings.";
    }

    private async Task LoadCategoriesAsync()
    {
        var allCats = (await _categoryService.GetCategoriesAsync()).ToList();
        var quickCats = (await _categoryService.GetQuickAccessCategoriesAsync()).ToList();
        AllCategories = new ObservableCollection<Category>(allCats);
        QuickCategories = new ObservableCollection<Category>(quickCats);
        _categoryNavStack.Clear();
        CurrentParentCategory = null;
        var rootCats = allCats.Where(c => c.Parent?.Id == null).ToList();
        CurrentDisplayedCategories = new ObservableCollection<Category>(rootCats);
        HasSubcategories = rootCats.Count > 0;
        IsViewingCategories = true;
        OnPropertyChanged(nameof(CanNavigateUp));
        _ = Task.WhenAll(allCats.Concat(quickCats).Where(c => !string.IsNullOrEmpty(c.ImageUrl)).Select(LoadCategoryImageAsync));
    }

    public PosViewModel(
        IProductService productService,
        ICategoryService categoryService,
        ICartService cartService,
        IPrinterService printerService,
        ICustomerDisplayService customerDisplayService,
        IShiftService shiftService,
        IOfflineStorageService offlineStorageService,
        ISyncService syncService,
        ISettingsService settingsService,
        IExpenseDocumentService expenseDocumentService,
        ICounterpartyService counterpartyService,
        IParkedSaleService parkedSaleService,
        IReturnService returnService,
        ICashOperationService cashOperationService,
        IQuoteService quoteService,
        IPromotionProvider promotionProvider,
        ISessionContext session,
        HttpClient httpClient,
        ISellerSession sellerSession,
        ISellerRosterService rosterService,
        IAuthService authService,
        ICashFeatureService features)
    {
        _promotionProvider = promotionProvider;
        _productService = productService;
        _categoryService = categoryService;
        _cartService = cartService;
        _printerService = printerService;
        _customerDisplayService = customerDisplayService;
        _shiftService = shiftService;
        _offlineStorageService = offlineStorageService;
        _syncService = syncService;
        _settingsService = settingsService;
        _expenseDocumentService = expenseDocumentService;
        _counterpartyService = counterpartyService;
        _parkedSaleService = parkedSaleService;
        _returnService = returnService;
        _cashOperationService = cashOperationService;
        _quoteService = quoteService;
        _session = session;
        _httpClient = httpClient;
        _sellerSession = sellerSession;
        _rosterService = rosterService;
        _authService = authService;
        _features = features;

        OpenCustomerRegistrationCommand = new AsyncRelayCommand(OpenCustomerRegistration);
        CloseApplicationCommand = new RelayCommand(CloseApplication);

        _cartService.CartChanged += OnCartChanged;
        _printerService.StatusChanged += OnPrinterStatusChanged;
        _sellerSession.CurrentChanged += OnSellerChanged;

        _ = InitializeAsync();

        // So the screen renders with the right visibility even before InitializeAsync's
        // own await resolves — see ApplyFeatures' remarks for why it runs a second time
        // once the cached map is actually loaded.
        ApplyFeatures();
    }

    /// <summary>Snapshots the flags into the bindings the screen reads. Called
    /// twice by design: once in the constructor, so the screen renders before any
    /// await resolves, and once from InitializeAsync after the local database is
    /// ready and the cached map has actually been loaded. Not called again after
    /// that — a flag must never change what an open scenario is doing (see this
    /// task's own rule: values are snapshotted when the screen opens, not re-read
    /// continuously).</summary>
    private void ApplyFeatures()
    {
        var features = _features.Current;
        IsReturnsEnabled = features.IsEnabled(CashFeatureCodes.Returns);
        IsParkingEnabled = features.IsEnabled(CashFeatureCodes.ParkedSales);
        IsMixedPaymentEnabled = features.IsEnabled(CashFeatureCodes.MixedPayment);
        IsCustomerRegistrationEnabled = features.IsEnabled(CashFeatureCodes.CustomerRegistration);
        IsSellerSwitchEnabled = features.IsEnabled(CashFeatureCodes.SellerSwitch);
        IsCustomerDisplayEnabled = features.IsEnabled(CashFeatureCodes.CustomerDisplay);
        IsDiscountEnabled = features.IsEnabled(CashFeatureCodes.Discount);
        IsCouponsEnabled = features.IsEnabled(CashFeatureCodes.Coupons);

        // These two read the map live instead of being snapshotted into a field, so
        // nothing else would tell the view they just changed. Without this the
        // constructor's optimistic pass — where an unconfigured code reads as
        // enabled — is the only value the screen ever binds, and a store with
        // cash_exchange_enabled off shows the button for the whole session.
        OnPropertyChanged(nameof(IsExchangeVisible));
        OnPropertyChanged(nameof(IsExchangeEnabled));
    }


    private void StartBackgroundSync()
    {
        _syncCancellationTokenSource?.Cancel();
        _syncCancellationTokenSource = new CancellationTokenSource();
        var token = _syncCancellationTokenSource.Token;

        _ = Task.Run(async () =>
        {
            DateTime lastSyncTime = DateTime.MinValue;

            while (!token.IsCancellationRequested)
            {
                // Ping the server every 10 seconds to update IsSystemOnline status
                await _syncService.CheckSystemOnlineAsync();

                int intervalMinutes = _settingsService.SyncIntervalMinutes;
                if (intervalMinutes <= 0) intervalMinutes = 10;

                // Sync products if enough time has passed
                if (DateTime.Now - lastSyncTime >= TimeSpan.FromMinutes(intervalMinutes))
                {
                    await _syncService.SyncProductsAsync();

                    // Roster changes (new hire, revoked seller, changed PIN) should reach
                    // the register on the same cadence as the product catalogue. This
                    // whole loop runs on a background thread (started via Task.Run above,
                    // no captured UI SynchronizationContext), so RefreshAsync — which only
                    // touches HTTP and SQLite — is safe to call directly here. It never
                    // throws (falls back to the cache, worst case an empty roster), so a
                    // roster-refresh problem can't take down the product sync around it.
                    // LoadRosterAsync mutates SellerSession state and asserts UI-thread
                    // access, so it must be marshalled via Dispatcher.UIThread — the same
                    // idiom OnProductsSynced below uses for the same reason.
                    var roster = await _rosterService.RefreshAsync();
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                        async () => await _sellerSession.LoadRosterAsync(roster));

                    lastSyncTime = DateTime.Now;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    private async Task InitializeAsync()
    {
        await _offlineStorageService.InitializeAsync();

        // Reading the cache any earlier is impossible: OfflineStorageService.InitializeAsync
        // is what creates the Settings table this reads from. RefreshAsync loads whatever
        // SyncService last cached; ApplyFeatures re-snapshots the flags now that the load
        // actually happened, replacing the constructor's pre-await guess.
        await _features.RefreshAsync();
        ApplyFeatures();

        // Load the cached promotions before the first cart change, so an offline
        // start still prices carts instead of waiting for the first sync.
        await _promotionProvider.RefreshAsync();

        _expenseDocumentService.UnsyncedDocumentsCountChanged += OnUnsyncedDocumentsCountChanged;
        UnsyncedDocumentsCount = await _expenseDocumentService.GetUnsyncedDocumentsCountAsync();

        _expenseDocumentService.SessionRevoked += OnSessionRevoked;
        _shiftService.SessionRevoked += OnShiftSessionRevoked;

        _parkedSaleService.CountChanged += OnParkedSaleCountChanged;
        ParkedSalesCount = await _parkedSaleService.GetCountAsync();

        _syncService.SyncStatusChanged += OnSyncStatusChanged;
        _syncService.ProductsSynced += OnProductsSynced;

        StartBackgroundSync();

        var allCats = (await _categoryService.GetCategoriesAsync()).ToList();
        var quickCats = (await _categoryService.GetQuickAccessCategoriesAsync()).ToList();
        AllCategories = new ObservableCollection<Category>(allCats);
        QuickCategories = new ObservableCollection<Category>(quickCats);
        var rootCats = allCats.Where(c => c.Parent?.Id == null).ToList();
        CurrentDisplayedCategories = new ObservableCollection<Category>(rootCats);
        HasSubcategories = rootCats.Count > 0;
        _ = Task.WhenAll(allCats.Concat(quickCats).Where(c => !string.IsNullOrEmpty(c.ImageUrl)).Select(LoadCategoryImageAsync));
        IsViewingCategories = true;

        Console.WriteLine("[PosViewModel] Calling GetShiftStateAsync during initialization.");
        System.Diagnostics.Debug.WriteLine("[PosViewModel] Calling GetShiftStateAsync during initialization.");
        CurrentShiftId = await _shiftService.GetShiftStateAsync();
        IsShiftOpen = !string.IsNullOrEmpty(CurrentShiftId);
        Console.WriteLine($"[PosViewModel] GetShiftStateAsync result: {IsShiftOpen} (ID: {CurrentShiftId})");
        System.Diagnostics.Debug.WriteLine($"[PosViewModel] GetShiftStateAsync result: {IsShiftOpen} (ID: {CurrentShiftId})");
        IsShiftModalVisible = !IsShiftOpen;

        if (IsShiftOpen)
        {
            // A register that restarts mid-shift must still get its roster before the
            // cashier can ring up a first receipt. InitializeAsync is kicked off
            // fire-and-forget from the constructor, which App.axaml.cs always calls on
            // the UI thread (NavigateToPos runs from UI-thread event handlers); with no
            // ConfigureAwait(false) anywhere in this codebase, every await above resumes
            // on that captured UI SynchronizationContext, so this is still the UI thread.
            //
            // StartBackgroundSync (called earlier in this same method) starts its loop
            // with lastSyncTime = DateTime.MinValue, so it also fires an immediate
            // roster refresh on a background thread around now. That overlap is
            // deliberately left in place, not coalesced away at the call site:
            // SellerRosterService.RefreshAsync itself coalesces concurrent callers onto
            // one in-flight fetch, so this and the background loop's call either share a
            // single HTTP round-trip (if they overlap) or each make their own
            // consistent one (if they don't) — never two racing round-trips where a
            // stale response could resolve later and overwrite a fresh one.
            await _sellerSession.LoadRosterAsync(await _rosterService.RefreshAsync());
        }

        // Initial view is just all categories
        Products.Clear();
    }

    private async Task LoadProductsAsync(string? categoryId)
    {
        var products = string.IsNullOrWhiteSpace(SearchQuery)
            ? await _productService.GetProductsByCategoryAsync(categoryId ?? "All")
            : await _productService.SearchProductsAsync(SearchQuery);
        System.Diagnostics.Debug.WriteLine($"[PosViewModel] LoadProductsAsync: {products.Count()} products for category '{categoryId}'");
        foreach (var p in products)
            System.Diagnostics.Debug.WriteLine($"[PosViewModel]   Product '{p.Name}' ImagePath='{p.ImagePath}' Category='{p.Category}'");
        Products = new ObservableCollection<Product>(products);
        _ = Task.WhenAll(products.Where(p => !string.IsNullOrEmpty(p.ImagePath)).Select(LoadProductImageAsync));
    }

    private async Task LoadProductImageAsync(Product product)
    {
        if (string.IsNullOrEmpty(product.ImagePath)) return;
        try
        {
            var backendUrl = _settingsService.BackendUrl;
            if (string.IsNullOrEmpty(backendUrl)) return;
            var uri = new Uri(backendUrl);
            var origin = $"{uri.Scheme}://{uri.Authority}";
            var url = $"{origin}/{product.ImagePath.TrimStart('/')}";
            System.Diagnostics.Debug.WriteLine($"[PosViewModel] Loading product image '{product.Name}': {url}");
            var bytes = await _httpClient.GetByteArrayAsync(url);
            using var ms = new MemoryStream(bytes);
            var bitmap = new Bitmap(ms);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => product.ImageBitmap = bitmap);
            System.Diagnostics.Debug.WriteLine($"[PosViewModel] Loaded product image '{product.Name}' OK");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PosViewModel] Failed product image '{product.Name}': {ex.Message}");
        }
    }

    private async Task LoadCategoryImageAsync(Category category)
    {
        if (string.IsNullOrEmpty(category.ImageUrl)) return;
        try
        {
            System.Diagnostics.Debug.WriteLine($"[PosViewModel] Loading image for '{category.Name}': {category.ImageUrl}");
            var bytes = await _httpClient.GetByteArrayAsync(category.ImageUrl);
            using var ms = new MemoryStream(bytes);
            var bitmap = new Bitmap(ms);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => category.ImageBitmap = bitmap);
            System.Diagnostics.Debug.WriteLine($"[PosViewModel] Loaded image for '{category.Name}'");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PosViewModel] Failed to load image for '{category.Name}' ({category.ImageUrl}): {ex.Message}");
        }
    }

    partial void OnSearchQueryChanged(string value)
    {
        if (AllCategories != null)
        {
            var currentLevelCats = CurrentParentCategory == null
                ? AllCategories.Where(c => c.Parent?.Id == null)
                : AllCategories.Where(c => c.Parent?.Id == CurrentParentCategory.Id);

            if (!string.IsNullOrWhiteSpace(value))
            {
                var lowerVal = value.ToLowerInvariant();
                currentLevelCats = currentLevelCats.Where(c => c.Name != null && c.Name.ToLowerInvariant().Contains(lowerVal));
            }

            var catsList = currentLevelCats.ToList();
            CurrentDisplayedCategories = new ObservableCollection<Category>(catsList);
            HasSubcategories = catsList.Count > 0;
        }

        _ = LoadProductsAsync(SelectedCategory?.Id);
    }

    private void OnCartChanged(object? sender, EventArgs e)
    {
        CartItems = new ObservableCollection<CartItem>(_cartService.Items);
        RefreshPromoChip();
        if (CartItems.Count == 1)
        {
            OrderNumber++;
            OrderDateTime = DateTime.Now.ToString("dd MMM, yyyy • HH:mm");
        }
        else if (!CartItems.Any())
        {
            OrderDateTime = string.Empty;
        }
        Subtotal = _cartService.Subtotal;
        OnPropertyChanged(nameof(CustomerDiscountAmount));

        ManualDiscountAmount = _cartService.ManualDiscountPercent > 0 
            ? (_cartService.ManualDiscountPercent / 100m * Subtotal) 
            : _cartService.ManualDiscountAmount;

        TotalDiscount = _cartService.TotalDiscount;
        TotalAmount = _cartService.TotalAmount;
        AppliedDiscountName = _cartService.AppliedDiscountName ?? string.Empty;

        if (CustomerDisplayViewModel != null && IsCustomerDisplayEnabled)
        {
            CustomerDisplayViewModel.Items = CartItems;
            CustomerDisplayViewModel.Total = TotalAmount;
            CustomerDisplayViewModel.IsIdle = !CartItems.Any();
        }

        _ = _customerDisplayService.ShowTotalAsync(TotalAmount);

        if (!_applyingQuoteResult)
            TriggerRequote();
    }

    private void TriggerRequote() => _ = RequoteSafeAsync();

    private async Task RequoteSafeAsync()
    {
        try
        {
            await RequoteDebouncedAsync();
        }
        catch (Exception ex)
        {
            // Detached task: a requote failure must not vanish silently nor crash.
            System.Diagnostics.Debug.WriteLine($"[PosViewModel] Requote failed: {ex}");
        }
    }

    private async Task RequoteDebouncedAsync()
    {
        _quoteCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        _quoteCts = cts;
        try { await Task.Delay(300, cts.Token); }
        catch (TaskCanceledException) { return; }
        await RequoteAsync(cts);
    }

    /// <summary>Quotes the cart immediately, skipping the debounce, and awaits the answer.
    /// Used right before payment: the debounced requote can still be pending when the
    /// cashier hits Pay, and the sale must report the quote it was actually priced from.
    /// Never lets a quote failure block a sale — the cart falls back to local pricing.</summary>
    private async Task RequoteNowAsync()
    {
        try
        {
            _quoteCts?.Cancel();
            var cts = new System.Threading.CancellationTokenSource();
            _quoteCts = cts;
            await RequoteAsync(cts);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PosViewModel] Pre-payment requote failed: {ex}");
        }
    }

    // Triggers all originate on the UI thread and the codebase never uses
    // ConfigureAwait(false), so these continuations resume on the UI thread —
    // required because applying a quote raises CartChanged, which rebuilds
    // UI-bound collections.
    private async Task RequoteAsync(System.Threading.CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var cardId = SelectedCustomer?.DiscountCard?.Identifier;

        // No card / no code is NOT a reason to skip the quote: auto-applied
        // promotions have no cashier input at all, and the server can only put
        // them into best-deal if it is asked to price the cart. Neither is a missing
        // warehouse id — the register never learns one, and the server resolves it
        // from the cash token (gating on it here is what kept this call from ever
        // being made).
        if (!IsSystemOnline || _cartService.Items.Count == 0)
        {
            // Logged, because the silent version of this branch is precisely how an
            // always-skipped quote went unnoticed: the cart just kept pricing locally.
            if (_cartService.Items.Count > 0)
                System.Diagnostics.Debug.WriteLine("[PosViewModel] Requote skipped: system offline.");
            if (IsCurrentQuote(cts)) ApplyQuoteGuarded(() => _cartService.ClearQuote());
            return;
        }

        var request = QuoteRequestBuilder.Build(_cartService.Items, _session.WarehouseId, cardId, _activePromoCode);
        var result = await _quoteService.QuoteAsync(request, ct);
        if (!IsCurrentQuote(cts)) return; // a newer requote superseded this one

        if (result == null)
        {
            // Network failure / rejected request: fall back to local pricing. QuoteService
            // logs why; without that this fallback is indistinguishable from being offline.
            System.Diagnostics.Debug.WriteLine("[PosViewModel] Requote returned no quote — pricing the cart locally.");
            ApplyQuoteGuarded(() => _cartService.ClearQuote());
            return;
        }

        ApplyQuoteGuarded(() => _cartService.ApplyQuote(result));

        if (result.Rejected.Count > 0)
        {
            StatusMessage = $"Промокод отклонён: {result.Rejected[0].Reason}";
            ClearActivePromo();
        }
        else if (!string.IsNullOrWhiteSpace(_activePromoCode) && result.Applied.Count > 0)
        {
            StatusMessage = "Промокод применён";
        }
    }

    // True only while cts is still the active request (not superseded by a newer
    // trigger nor cancelled), so a stale in-flight quote can never apply after a newer one.
    private bool IsCurrentQuote(System.Threading.CancellationTokenSource cts)
        => ReferenceEquals(_quoteCts, cts) && !cts.IsCancellationRequested;

    // The promo path is now server-driven via _activePromoCode (not _cartService coupons),
    // so the coupon-chip UI (bound to AppliedCoupons) mirrors the active promo code.
    private void RefreshPromoChip()
    {
        AppliedCoupons = string.IsNullOrWhiteSpace(_activePromoCode)
            ? new ObservableCollection<Coupon>()
            : new ObservableCollection<Coupon> { new Coupon { Code = _activePromoCode } };
    }

    private void ClearActivePromo()
    {
        _activePromoCode = null;
        RefreshPromoChip();
    }

    // Guard against recursion: ApplyQuote/ClearQuote raise CartChanged ->
    // OnCartChanged must not start another requote.
    private void ApplyQuoteGuarded(System.Action apply)
    {
        _applyingQuoteResult = true;
        try { apply(); }
        finally { _applyingQuoteResult = false; }
    }

    private void OnPrinterStatusChanged(object? sender, PrinterStatus status)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsPrinterReady = status == PrinterStatus.Ready;
            PrinterStatusText = status switch
            {
                PrinterStatus.Ready => "Printer Ready",
                PrinterStatus.NoPaper => "No Paper",
                PrinterStatus.Error => "Printer Error",
                PrinterStatus.Offline => "Printer Offline",
                _ => "Unknown"
            };
        });
    }

    private void OnUnsyncedDocumentsCountChanged(object? sender, int count)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => UnsyncedDocumentsCount = count);
    }

    /// <summary>SyncOfflineDocumentsAsync's loop runs on a background thread, so — same
    /// idiom as every other handler in this file that reacts to a background-thread event
    /// (OnUnsyncedDocumentsCountChanged right above, OnSyncStatusChanged, etc.) — this
    /// marshals onto the UI thread via Dispatcher.UIThread before touching the
    /// UI-bound IsSessionRevoked property.</summary>
    private void OnSessionRevoked(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSessionRevoked = true);
    }

    /// <summary>Unlike <see cref="OnSessionRevoked"/> above — which only raises a banner
    /// because a queued receipt might be mid-flight — ShiftService.SessionRevoked fires from
    /// GetShiftStateAsync (startup) or OpenShiftAsync (the shift modal's own button), and both
    /// only ever run while nothing is mid-receipt: either the register just launched, or it is
    /// already blocked behind the shift modal with no way for that modal to ever succeed on a
    /// dead token. There is nothing to protect by staying put, so this completes the sign-out
    /// immediately (see <see cref="PerformSignOut"/>) instead of leaving the cashier stuck.
    /// Marshals to the UI thread for the same reason as every other handler here — ShiftService
    /// posts this event, not invokes it inline (see its own NotifySessionRevoked remarks).</summary>
    private void OnShiftSessionRevoked(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => PerformSignOut(I18nService.Instance["SessionExpiredSignInAgain"]));
    }

    private void OnParkedSaleCountChanged(object? sender, int count)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ParkedSalesCount = count);
    }

    private void OnSyncStatusChanged(object? sender, bool isOnline)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSystemOnline = isOnline);
    }

    private async void OnProductsSynced(object? sender, EventArgs e)
    {
        // The sync that just finished also refreshed the promotion cache in SQLite.
        await _promotionProvider.RefreshAsync();

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await LoadCategoriesAsync();
            await LoadProductsAsync(SelectedCategory?.Id);
        });
    }

    public void Dispose()
    {
        // PosViewModel is transient but subscribes to singleton services; without this,
        // every NavigateToPos (e.g. logout->login) leaks the prior instance, which keeps
        // reacting to events and pinging the server via the background sync loop.
        _cartService.CartChanged -= OnCartChanged;
        _printerService.StatusChanged -= OnPrinterStatusChanged;
        _expenseDocumentService.UnsyncedDocumentsCountChanged -= OnUnsyncedDocumentsCountChanged;
        _expenseDocumentService.SessionRevoked -= OnSessionRevoked;
        _shiftService.SessionRevoked -= OnShiftSessionRevoked;
        _parkedSaleService.CountChanged -= OnParkedSaleCountChanged;
        _syncService.SyncStatusChanged -= OnSyncStatusChanged;
        _syncService.ProductsSynced -= OnProductsSynced;
        _sellerSession.CurrentChanged -= OnSellerChanged;

        _syncCancellationTokenSource?.Cancel();
        _syncCancellationTokenSource?.Dispose();
        _syncCancellationTokenSource = null;

        // Cancel any in-flight debounced requote so it can't mutate a disposed VM.
        _quoteCts?.Cancel();
        _quoteCts?.Dispose();
        _quoteCts = null;
    }

    [RelayCommand]
    private async Task SearchProducts()
    {
        await LoadProductsAsync(SelectedCategory?.Id);
    }

    [RelayCommand]
    private async Task SelectCategory(Category? category)
    {
        SearchQuery = string.Empty;

        if (category == null)
        {
            _categoryNavStack.Clear();
            CurrentParentCategory = null;
            var rootCats = AllCategories.Where(c => c.Parent?.Id == null).ToList();
            CurrentDisplayedCategories = new ObservableCollection<Category>(rootCats);
            HasSubcategories = rootCats.Count > 0;
            SelectedCategory = null;
            IsViewingCategories = true;
            Products.Clear();
            OnPropertyChanged(nameof(CanNavigateUp));
            return;
        }

        var children = AllCategories.Where(c => c.Parent?.Id == category.Id).ToList();
        _categoryNavStack.Push(CurrentParentCategory);
        CurrentParentCategory = category;
        CurrentDisplayedCategories = new ObservableCollection<Category>(children);
        HasSubcategories = children.Count > 0;
        SelectedCategory = category;
        IsViewingCategories = false;
        OnPropertyChanged(nameof(CanNavigateUp));
        await LoadProductsAsync(category.Id);
    }

    [RelayCommand]
    private async Task NavigateCategoryUp()
    {
        if (_categoryNavStack.Count == 0) return;
        var parent = _categoryNavStack.Pop();
        CurrentParentCategory = parent;
        var cats = parent == null
            ? AllCategories.Where(c => c.Parent?.Id == null)
            : AllCategories.Where(c => c.Parent?.Id == parent.Id);
        var catsList = cats.ToList();
        CurrentDisplayedCategories = new ObservableCollection<Category>(catsList);
        HasSubcategories = catsList.Count > 0;
        SelectedCategory = parent;
        IsViewingCategories = parent == null;
        OnPropertyChanged(nameof(CanNavigateUp));
        if (parent != null)
            await LoadProductsAsync(parent.Id);
        else
            Products.Clear();
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = string.Empty;
    }

    [RelayCommand]
    private void AddToCart(Product product)
    {
        // Ask who is selling only at the start of a receipt: an empty cart plus a stale
        // session means nobody has confirmed identity since the idle timeout, so raise the
        // overlay request before this item lands. Once the cart has an item, the receipt is
        // in progress and must never be interrupted for this — same reasoning as Touch()
        // below keeping the session alive through the rest of the sale.
        // With seller switching disabled there is no separate identity to confirm —
        // everything on this register is the shift owner's — so the start-of-receipt
        // ask never fires.
        //
        // canSignOut is a hard false, never CanEndSellerSession: the cart is only empty
        // right here because that is this gate's own firing condition — product is about
        // to land in it a few lines down. Reading CanEndSellerSession at this instant
        // would read true and stay wrong for as long as the overlay is actually showing
        // (see SellerSwitchRequest's remarks).
        if (IsSellerSwitchEnabled && !_cartService.Items.Any() && _sellerSession.IsStale)
            SellerSwitchRequested?.Invoke(this, new SellerSwitchRequest(canSignOut: false));

        // Any add is genuine register activity, not just the first one — resets the idle
        // timer so a long receipt never goes stale mid-sale.
        _sellerSession.Touch();

        _cartService.AddProduct(product);
        _ = _customerDisplayService.ShowItemAsync(product.Name, product.Price);
    }

    [RelayCommand]
    private void RemoveFromCart(CartItem item)
    {
        _cartService.RemoveItem(item);
    }

    [RelayCommand]
    private void IncreaseQuantity(CartItem item)
    {
        _cartService.IncreaseQuantity(item);
    }

    [RelayCommand]
    private void DecreaseQuantity(CartItem item)
    {
        _cartService.DecreaseQuantity(item);
    }

    [RelayCommand]
    private void ClearCart()
    {
        _cartService.ClearCart();
        _cartService.ClearCustomerDiscount();
        SelectedCustomer = null;
        ClearActivePromo();
        _approvedById = null;
        _ = _customerDisplayService.ClearAsync();

        // Only this command — the cashier deliberately dropping the receipt. The
        // internal _cartService.ClearCart() calls (park, auto-park inside
        // ResumeParkedSale) are mid-operation, not the end of one, and must not reset.
        EndReceipt();
    }

    [RelayCommand]
    private Task ApplyCoupon()
    {
        if (string.IsNullOrWhiteSpace(CouponCode))
        {
            IsCouponModalVisible = false;
            return Task.CompletedTask;
        }
        // Redesign's coupon modal, but the code is now resolved server-side via the
        // quote (not the old mock DiscountService): stash it and re-quote.
        _activePromoCode = CouponCode.Trim();
        RefreshPromoChip(); // show the entered code immediately (removed if server rejects)
        StatusMessage = $"Проверка кода: {_activePromoCode}…";
        CouponCode = string.Empty;
        IsCouponModalVisible = false;
        TriggerRequote();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenCouponModal()
    {
        // Disabled entry points are hidden, not greyed out (see PosView.axaml's binding
        // on this command's button) — but the command itself must also refuse, since a
        // stray click on a control mid-hide-animation, or any other path that still
        // reaches this method, must not open a modal this register's flag says doesn't
        // apply here (see OpenSellerSwitch's own remarks for the same rule).
        if (!IsCouponsEnabled) return;

        CouponCode = string.Empty;
        IsCouponModalVisible = true;
    }

    [RelayCommand]
    private void CloseCouponModal()
    {
        IsCouponModalVisible = false;
    }

    [RelayCommand]
    private void RemoveCoupon(string code)
    {
        ClearActivePromo();
        _cartService.RemoveCoupon(code);
        TriggerRequote();
    }

    [RelayCommand]
    private void OpenDiscountModal()
    {
        // Same guard as OpenCouponModal above, and OpenSellerSwitch before that: this
        // flag hides the manual-discount button only — customer-category discounts and
        // automatic promotions never go through this command, so gating it cannot make
        // an offline total disagree with the server's.
        if (!IsDiscountEnabled) return;

        DiscountInputValue = string.Empty;
        IsDiscountModalVisible = true;
    }

    [RelayCommand]
    private void CloseDiscountModal()
    {
        IsDiscountModalVisible = false;
    }

    [RelayCommand]
    private void OpenQuantityPad(CartItem item)
    {
        QuantityPad = new QuantityPadViewModel(item);
        IsQuantityPadVisible = true;
    }

    [RelayCommand]
    private void CloseQuantityPad()
    {
        IsQuantityPadVisible = false;
        QuantityPad = null;
    }

    [RelayCommand]
    private void ConfirmQuantityPad()
    {
        QuantityPad?.Commit(_cartService);
        CloseQuantityPad();
    }

    [RelayCommand]
    private void QuantityPadAppend(string digit) => QuantityPad?.Append(digit);

    [RelayCommand]
    private void QuantityPadBackspace() => QuantityPad?.Backspace();

    [RelayCommand]
    private void QuantityPadClear() => QuantityPad?.Clear();

    [RelayCommand]
    private void AppendDiscountInput(string value)
    {
        if (value == "BACKSPACE")
        {
            if (!string.IsNullOrEmpty(DiscountInputValue))
            {
                DiscountInputValue = DiscountInputValue.Substring(0, DiscountInputValue.Length - 1);
            }
        }
        else if (value == "CLEAR")
        {
            DiscountInputValue = string.Empty;
        }
        else
        {
            DiscountInputValue += value;
        }
    }

    /// <summary>A manual discount above the current seller's own cap needs a supervisor's
    /// approval. <c>MaxDiscount == 0</c> means "no personal cap configured", not "no
    /// discounts allowed" — right after the seller-PIN migration every seller has no cap
    /// (see the max_discount column's own remarks in the design spec), so treating 0 as a
    /// limit would demand a supervisor PIN for every manual discount from day one. Only
    /// gates when a cap is actually set. <paramref name="percent"/>-only: amount-mode
    /// discounts (see <see cref="IsDiscountAmountMode"/>) aren't compared against a
    /// percent cap and are out of scope here, same as before this task.</summary>
    private bool NeedsDiscountApproval(decimal percent)
    {
        var cap = _sellerSession.Current?.MaxDiscount ?? 0m;
        return cap > 0m && percent > cap;
    }

    [RelayCommand]
    private void ApplyManualDiscount()
    {
        if (!decimal.TryParse(DiscountInputValue, out var value))
        {
            CloseDiscountModal();
            return;
        }

        // Same seller-switch-off exception as CloseShift/OpenReturns: with no separate
        // sellers there is nobody else's approval to escalate to, and the overlay that
        // would collect it is hidden along with the flag.
        if (IsSellerSwitchEnabled && IsDiscountPercentMode && NeedsDiscountApproval(value))
        {
            CloseDiscountModal();
            DiscountApprovalRequested?.Invoke(this, value);
            return;
        }

        // A fresh discount that didn't need escalation invalidates whatever approval
        // was recorded for a previous one on this same receipt (see _approvedById) —
        // otherwise a stale approver id could end up attached to a discount nobody
        // with a covering cap ever actually signed off on.
        _approvedById = null;

        if (IsDiscountPercentMode)
        {
            _cartService.SetManualDiscount(value, 0);
        }
        else
        {
            _cartService.SetManualDiscount(0, value);
        }
        CloseDiscountModal();
    }

    /// <summary>Continuation for <see cref="DiscountApprovalRequested"/> — applies the
    /// percent discount that triggered the escalation and records who approved it, so
    /// <see cref="Pay"/> can stamp it onto the outgoing <see cref="DocumentRequest.ApprovedBy"/>.
    /// See <see cref="_approvedById"/> for how its lifetime is kept scoped to this receipt.</summary>
    public void ApplyApprovedDiscount(string approverId, decimal percent)
    {
        _approvedById = approverId;
        _cartService.SetManualDiscount(percent, 0);
    }

    [RelayCommand]
    private void ClearManualDiscount()
    {
        _approvedById = null;
        _cartService.ClearManualDiscount();
        CloseDiscountModal();
    }

    [RelayCommand]
    private async Task PrintReceipt()
    {
        if (!CartItems.Any()) return;
        var success = await _printerService.PrintReceiptAsync(
            _cartService.Items,
            Subtotal, TotalDiscount, TotalAmount,
            _cartService.AppliedCoupons,
            _cartService.AppliedDiscountName);
        StatusMessage = success ? "Receipt printed." : "Print failed.";
    }

    [RelayCommand]
    private async Task OpenCustomerSearch()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                var dialog = new VvCash.Views.CustomerSearchWindow();
                dialog.DataContext = new CustomerSearchViewModel(dialog, _counterpartyService);
                var result = (VvCash.Models.Api.CounterpartyResponse?) await dialog.ShowDialog<object>(mainWindow);
                if (result != null)
                {
                    SelectedCustomer = result;
                    if (result.DiscountCard != null && result.DiscountCard.Discount > 0)
                    {
                        _cartService.SetCustomerDiscount(result.DiscountCard.Discount); // offline fallback
                        StatusMessage = $"Клиент: {result.FullName} • Скидка по карте: {result.DiscountCard.Discount}%";
                    }
                    else
                    {
                        _cartService.ClearCustomerDiscount();
                        StatusMessage = $"Выбран клиент: {result.FullName}";
                    }
                    TriggerRequote();
                }
            }
        }
    }

    private async Task OpenCustomerRegistration()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                var dialog = new VvCash.Views.CustomerRegistrationWindow();
                dialog.DataContext = new CustomerRegistrationViewModel(dialog, _counterpartyService);
                await dialog.ShowDialog(mainWindow);
            }
        }
    }

    private ParkedSaleSnapshot BuildSnapshot(string? label) => new()
    {
        Items = _cartService.Items
            .Select(i => new ParkedCartItem
            {
                Product = i.Product,
                Quantity = i.Quantity,
                QuantityInUnit = i.QuantityInUnit,
                EnteredInUnit = i.EnteredInUnit,
            })
            .ToList(),
        ManualDiscountPercent = _cartService.ManualDiscountPercent,
        ManualDiscountAmount = _cartService.ManualDiscountAmount,
        CustomerDiscountPercent = _cartService.CustomerDiscountPercent,
        AppliedCoupons = _cartService.AppliedCoupons.ToList(),
        Customer = SelectedCustomer,
        Label = label,
        // Carry any approval that already happened along with the discount it authorised
        // — see _approvedById's remarks on why this is the one place it is not reset.
        ApprovedById = _approvedById
    };

    [RelayCommand]
    private void OpenParkLabelModal()
    {
        if (!CartItems.Any()) return;
        ParkLabelInput = string.Empty;
        IsParkLabelModalVisible = true;
    }

    [RelayCommand]
    private void CloseParkLabelModal()
    {
        IsParkLabelModalVisible = false;
    }

    [RelayCommand]
    private async Task ConfirmParkSale()
    {
        if (!CartItems.Any()) { IsParkLabelModalVisible = false; return; }

        await _parkedSaleService.ParkAsync(BuildSnapshot(ParkLabelInput), TotalAmount);

        _cartService.ClearCart();
        _cartService.ClearCustomerDiscount();
        SelectedCustomer = null;
        ClearActivePromo();
        _approvedById = null;
        _ = _customerDisplayService.ClearAsync();

        IsParkLabelModalVisible = false;
        StatusMessage = "Чек отложен.";
    }

    [RelayCommand]
    private async Task OpenParkedSales()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                var dialog = new VvCash.Views.ParkedSalesWindow();
                dialog.DataContext = new ParkedSalesViewModel(dialog, _parkedSaleService);
                var result = await dialog.ShowDialog<object?>(mainWindow);
                if (result is string id)
                {
                    await ResumeParkedSale(id);
                }
            }
        }
    }

    [RelayCommand]
    private async Task OpenReturns()
    {
        // Opening returns requires CanRefund. Nobody having confirmed at all
        // (Current == null) is treated the same as lacking the right — fail closed,
        // same reasoning CloseShift already uses for CanCloseShift. A seller who
        // lacks it must escalate through a supervisor PIN instead: raise intent and
        // let the host (App.axaml.cs) open the overlay in approval mode.
        //
        // Same seller-switch-off exception as CloseShift: with no separate sellers,
        // the shift owner's rights are the only rights, and the overlay that could
        // satisfy this gate is hidden along with the flag.
        if (IsSellerSwitchEnabled && !(_sellerSession.Current?.CanRefund ?? false))
        {
            RefundApprovalRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        await ShowReturnsDialogAsync();
    }

    /// <summary>Actually opens the returns dialog — the direct target of
    /// <see cref="OpenReturns"/> once the current seller already holds CanRefund, and
    /// also the continuation App.axaml.cs hands to <c>SellerSwitchViewModel.OpenForApproval</c>
    /// for <see cref="RefundApprovalRequested"/>, so a successful approval genuinely opens
    /// returns rather than merely dismissing the overlay.</summary>
    public async Task ShowReturnsDialogAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                var dialog = new VvCash.Views.ReturnsWindow();
                var returnsVm = new ReturnsViewModel(dialog, _returnService, _printerService, _settingsService, _features);
                dialog.DataContext = returnsVm;
                await dialog.ShowDialog(mainWindow);

                // A returns screen that actually booked something ends the operation the
                // same way a payment does — see EndReceipt. Opened and closed without a
                // return, it costs nothing.
                if (returnsVm.HasBookedDocument) EndReceipt();
            }
        }
    }

    [RelayCommand]
    private async Task OpenExchange()
    {
        // An exchange writes a replacement-sale document that carries seller_id, and
        // ExchangeViewModel snapshots _sellerSession.Current?.Id into its constructor below
        // — so opening this screen with nobody confirmed produces a sale silently credited
        // to the shift owner, with nothing on screen saying so. That is exactly the
        // consequence this branch introduced: before EndReceipt() started clearing Current
        // after every completed operation, a confirmed seller survived between receipts and
        // an exchange usually carried someone; now the very next customer's "actually, I'd
        // like to exchange this" reaches an empty session every time.
        //
        // This is a "who is selling" gate, not a rights escalation, which is why it raises
        // SellerSwitchRequested rather than RefundApprovalRequested/CloseShiftApprovalRequested
        // the way OpenReturns/CloseShift do above. Those two exist so a supervisor can approve
        // on someone else's behalf without becoming the current seller — the right being
        // checked (CanRefund/CanCloseShift) is independent of whose id ends up on the
        // document. Here there is no separate right to escalate: the whole problem is that
        // nobody's id is available to stamp at all, so the fix is to make someone become
        // Current, which is what SellerSwitchRequested does and what approval mode
        // deliberately does not.
        //
        // The overlay is non-blocking and carries no continuation (unlike the approval
        // flows' OpenForApproval), so this press does not open the exchange window — the
        // cashier confirms who is selling and taps Exchange again, the same shape as
        // AddToCart, which also asks and lets the next action proceed.
        //
        // Same seller-switch-off exception as everywhere else: with IsSellerSwitchEnabled
        // false there is no separate identity to confirm and the overlay itself is hidden,
        // so the gate must not fire.
        //
        // canSignOut: false, same as the other automatic gates — this method never fills
        // the POS cart itself (the exchange window is separate), so CanEndSellerSession
        // would in fact stay accurate for as long as this particular overlay is showing;
        // false anyway, to keep one simple rule (only the manual chip tap in
        // OpenSellerSwitch may grant sign-out) rather than a per-site judgment call about
        // which automatic gates happen to be safe today and might stop being tomorrow.
        if (IsSellerSwitchEnabled && _sellerSession.IsStale)
        {
            SellerSwitchRequested?.Invoke(this, new SellerSwitchRequest(canSignOut: false));
            return;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                var dialog = new VvCash.Views.ExchangeWindow();
                var exchangeVm = new ExchangeViewModel(
                    dialog, _returnService, _cashOperationService, _expenseDocumentService,
                    _counterpartyService, _settingsService, _productService, _syncService,
                    _printerService, _features,
                    _promotionProvider.MoneyPolicy, CurrentShiftId ?? string.Empty,
                    _sellerSession.Current?.Id, _session.CashId, IsSystemOnline);
                dialog.DataContext = exchangeVm;
                await dialog.ShowDialog(mainWindow);

                // Same rule as returns above. The seller id the exchange documents carry
                // was snapshotted at construction time, so clearing here cannot affect
                // what was already sent.
                if (exchangeVm.HasBookedDocument) EndReceipt();
            }
        }
    }

    /// <summary>The direct target of <see cref="OpenParkedSales"/> once a parked sale is
    /// picked from the dialog — public (like <see cref="ShowReturnsDialogAsync"/> and
    /// <see cref="OnCloseShiftApproved"/>) so it is reachable from a unit test without a
    /// running Avalonia application, since that dialog itself is not.
    ///
    /// Resuming fills the cart directly, without ever going through
    /// <see cref="AddToCart"/> — so a stale/absent session (e.g. the register just
    /// restarted and the cashier's first action is resuming a parked sale, never scanning
    /// a product) would otherwise reach <see cref="Pay"/> with nobody confirmed, and the
    /// resulting sale gets silently credited to the shift owner with no flag anywhere (see
    /// this task's own write-up). Fixed by applying the exact same start-of-receipt gate
    /// here: whenever this method is about to hand the cart a set of items nobody has
    /// rung up yet — which, by the time we reach the check below, is unconditionally true:
    /// either the cart started empty, or the auto-park branch just emptied it — ask if the
    /// session is stale, exactly like AddToCart's own gate. Checked, and the overlay
    /// requested, before <see cref="ISellerSession.Touch"/> and before the resumed items
    /// land in the cart: Touch() would clear staleness and defeat the check (same ordering
    /// AddToCart depends on), and asking before the cart visibly fills mirrors "ask at the
    /// start of the receipt" rather than after the cashier can already see it. The gate
    /// runs at most once per resumed receipt — there is exactly one check per call, not
    /// one per auto-park-then-resume step.
    ///
    /// Restores <see cref="ParkedSaleSnapshot.ApprovedById"/> into <see cref="_approvedById"/>
    /// after the rest of the snapshot loads, so an over-cap discount that was already
    /// approved before parking keeps its approver on resume instead of riding through with
    /// approved_by silently null. A snapshot parked by a build that predates this field
    /// deserializes ApprovedById as null (System.Text.Json's default for a missing
    /// property), which this assigns through unchanged — no approver, same as a discount
    /// that never needed one; never a crash. If the cashier subsequently raises the
    /// discount further, ApplyManualDiscount re-gates it through NeedsDiscountApproval same
    /// as any fresh discount; if they clear it, ClearManualDiscount drops this approver
    /// same as it always has. That approver id is about who authorised a discount, not who
    /// is selling — entirely independent of the seller gate added here, which never reads
    /// or writes <see cref="_approvedById"/>.
    ///
    /// Deliberately does NOT record which seller parked the sale: the person resuming is
    /// not necessarily the person who parked it (a sale parked at end of shift is
    /// routinely resumed by whoever is on register next), so crediting the resumed sale to
    /// whoever parked it would just be a different, equally silent mis-attribution — the
    /// same failure this whole feature exists to prevent, one step removed. Re-asking who
    /// is selling right now, same as any other start of a receipt, is the only choice that
    /// can't misattribute the resumed sale.</summary>
    public async Task ResumeParkedSale(string id)
    {
        // Если в корзине уже есть товары — авто-отложить текущую продажу.
        if (_cartService.Items.Any())
        {
            await _parkedSaleService.ParkAsync(BuildSnapshot(null), TotalAmount);
            _cartService.ClearCart();
            _cartService.ClearCustomerDiscount();
            SelectedCustomer = null;
            ClearActivePromo();
            _approvedById = null;
        }

        var snapshot = await _parkedSaleService.ResumeAsync(id);
        if (snapshot == null) return;

        // Same start-of-receipt gate as AddToCart, applied here because resuming skips
        // AddToCart entirely — see this method's own remarks for why this must run before
        // Touch() and before the cart is populated below. Same seller-switch-off
        // exception as AddToCart too: no separate identity to confirm when the flag is off.
        //
        // canSignOut: false, same reasoning as AddToCart's own gate — the cart is empty
        // right here (either it started that way, or the auto-park branch above just
        // emptied it) but the resumed snapshot's items are about to land in it below, so
        // CanEndSellerSession would read true now and be wrong for as long as the overlay
        // is actually showing.
        if (IsSellerSwitchEnabled && _sellerSession.IsStale)
            SellerSwitchRequested?.Invoke(this, new SellerSwitchRequest(canSignOut: false));

        // Resuming a parked sale is genuine register activity, same as AddToCart's own
        // Touch() call — resets the idle timer so a long-parked receipt that just got
        // pulled back up doesn't immediately look stale again.
        _sellerSession.Touch();

        var items = snapshot.Items
            .Select(i => new CartItem
            {
                Product = i.Product,
                Quantity = i.Quantity,
                QuantityInUnit = i.QuantityInUnit,
                EnteredInUnit = i.EnteredInUnit,
            })
            .ToList();

        // Set the customer before LoadSnapshot so the CartChanged cascade sees
        // the restored customer (matches the normal customer-select flow).
        SelectedCustomer = snapshot.Customer;

        _cartService.LoadSnapshot(
            items,
            snapshot.ManualDiscountPercent, snapshot.ManualDiscountAmount,
            snapshot.CustomerDiscountPercent,
            snapshot.AppliedCoupons);

        _approvedById = snapshot.ApprovedById;

        TriggerRequote();

        StatusMessage = "Отложенный чек возвращён.";
    }

    [RelayCommand]
    private async Task Pay()
    {
        if (!CartItems.Any()) return;

        if (string.IsNullOrEmpty(CurrentShiftId))
        {
            AlertMessage = "Cannot process payment: No active shift.";
            IsAlertModalVisible = true;
            return;
        }

        if (NavigationRequest != null)
        {
            // Before the amount to collect is shown, not after: the payment screen is
            // built from TotalAmount, so quoting afterwards would take money against a
            // price the server never agreed to.
            await RequoteNowAsync();

            var mixedPaymentVm = new MixedPaymentViewModel(TotalAmount, async (result, cashAmount, cardAmount) =>
            {
                if (result)
                {
                    var request = new DocumentRequest
                    {
                        DocumentHash = Guid.NewGuid().ToString(),
                        SellerId = _sellerSession.Current?.Id,
                        ApprovedBy = _approvedById,
                        ShiftId = CurrentShiftId,
                        QuoteId = _cartService.QuoteId,
                        OfflinePromotionId = _cartService.OfflinePromotion?.PromotionId,
                        SoldSource = SoldSourcesEnum.CASH,
                        Payment = new Payment
                        {
                            ToPay = TotalAmount,
                            PaidInCash = cashAmount,
                            PaidByCreditCard = cardAmount,
                            DiscountType = "cash",
                            Discount = TotalDiscount,
                            Remained = Math.Max(0, TotalAmount - (cashAmount + cardAmount))
                        },
                        Products = _cartService.Items.Select((item, lineIndex) =>
                        {
                            var (pct, before) = QuoteLineResolver.Resolve(
                                _cartService.Quote, _cartService.OfflinePromotion, item, lineIndex,
                                _cartService.MoneyPolicy);
                            return new DocumentProduct
                            {
                                Name = item.Product.Name,
                                ProductId = item.Product.Id,
                                Quantity = item.Quantity,
                                // The quoted price when a quote priced this line, the cached
                                // one otherwise. The server flags a line is_suspicious when
                                // sell_price differs from its catalog price, so sending a
                                // stale cached price would flag every honest sale.
                                SellPrice = item.UnitPrice,
                                PriceBeforeDiscount = before,
                                DiscountPercent = pct,
                                // All three or none: the server rejects a partial trio.
                                UnitId = item.Product.HasSecondaryUnit ? item.Product.UnitId : null,
                                UnitFactor = item.Product.HasSecondaryUnit ? item.Product.UnitFactor : null,
                                QuantityInUnit = item.Product.HasSecondaryUnit ? item.QuantityInUnit : null,
                            };
                        }).ToList()
                    };

                    StatusMessage = "Creating expense document...";
                    var success = await _expenseDocumentService.CreateExpenseDocumentAsync(request);

                    if (success)
                    {
                        await _printerService.PrintReceiptAsync(
                            _cartService.Items,
                            Subtotal, TotalDiscount, TotalAmount,
                            _cartService.AppliedCoupons,
                            _cartService.AppliedDiscountName);
                        _cartService.ClearCart();
                        _cartService.ClearCustomerDiscount();
                        SelectedCustomer = null;
                        ClearActivePromo();
                        _approvedById = null;
                        // The receipt is done and the document (posted or queued offline)
                        // already carries this seller's id — from here on nobody is
                        // confirmed. Only on this success branch: see EndReceipt.
                        EndReceipt();
                        StatusMessage = "Payment processed. Thank you!";

                        if (CustomerDisplayViewModel != null && IsCustomerDisplayEnabled)
                        {
                            CustomerDisplayViewModel.IsIdle = true;
                            CustomerDisplayViewModel.WelcomeMessage = "Thank you! Come again!";
                        }
                        _ = _customerDisplayService.ShowLineAsync("Thank you!", "Come again!");
                    }
                    else
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            AlertMessage = "Failed to create expense document on the server. Please try again.";
                            IsAlertModalVisible = true;
                            StatusMessage = "Payment failed.";
                        });
                    }
                }

                // Return to POS View
                NavigationRequest(this);
            }, IsMixedPaymentEnabled);

            NavigationRequest(mixedPaymentVm);
        }
    }

    public async Task HandleBarcodeAsync(string barcode)
    {
        var product = await _productService.GetProductByBarcodeAsync(barcode);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (product != null)
            {
                AddToCart(product);
            }
            else
            {
                AlertMessage = $"Товар со штрихкодом {barcode} не найден";
                IsAlertModalVisible = true;
                StatusMessage = AlertMessage;
            }
        });
    }
}
