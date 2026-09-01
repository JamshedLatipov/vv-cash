using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
using VvCash.Services.Queue;
using VvCash.Services.Update;

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

    /// <summary>What to run once somebody has actually confirmed, or null when the raise
    /// site has nothing waiting on the answer. Exists because the only gate left is
    /// <see cref="PosViewModel.Pay"/>'s: refusing a payment and raising the overlay leaves
    /// the cashier holding a press that did nothing, and without this they have to work out
    /// for themselves that the same button needs pressing again once the PIN is in. Carried
    /// on the request rather than resolved by the host for the same reason
    /// <see cref="CanSignOut"/> is — the raise site is the only thing that knows what it
    /// was in the middle of.</summary>
    public Func<SellerInfo, Task>? OnSwitched { get; }

    public SellerSwitchRequest(bool canSignOut, Func<SellerInfo, Task>? onSwitched = null)
    {
        CanSignOut = canSignOut;
        OnSwitched = onSwitched;
    }
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

    /// <summary>Постановка заказа в очередь (Task 22). Nullable: кассу можно собрать вовсе
    /// без очереди (см. IQueueClient docstring — тот же принцип, что и у остальных
    /// hardware-заглушек этого класса). Не IDisposable и ничего не подписывает, так что
    /// Dispose() этого класса ему ничего не должен — в отличие от _printerService и
    /// остальных полей выше, которых Dispose() ниже явно отписывает.</summary>
    private readonly IQueueClient? _queueClient;

    /// <summary>Important 3/9 fix: whether a number is needed and whether the order goes
    /// into the outbox are two different questions, and this is what answers the second
    /// one (see ProceedToPayAsync's own remarks at the call site for the first).
    /// Nullable for the same reason _queueClient is — a register can be built without a
    /// queue at all — and a null value reads the same as QueueRole.Off: no settings, no
    /// network queue to speak of.</summary>
    private readonly IQueueSettings? _queueSettings;
    private CancellationTokenSource? _syncCancellationTokenSource;
    private System.Threading.CancellationTokenSource? _quoteCts;
    private CancellationTokenSource? _searchCts;

    /// <summary>Whether a receipt is currently open, i.e. whether the cart last had
    /// anything in it. Only read by <see cref="OnCartChanged"/>, to tell "a receipt just
    /// began" apart from "a receipt that was already open changed".</summary>
    private bool _receiptOpen;
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

    /// <summary>Task 25: заказов в исходящем буфере очереди этой кассы —
    /// поставлены, но ещё не подтверждены соседней кассой-сервером.
    /// Отдельный счётчик от UnsyncedDocumentsCount выше, не сложенный с ним:
    /// не дошедший до бэкенда чек и не дошедший до соседней кассы заказ чинят
    /// разные люди разными действиями (см. IQueueClient.PendingCountAsync),
    /// и один номер над обоими отправил бы кассира не туда.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingQueueOrders))]
    private int _pendingQueueOrdersCount;

    public bool HasPendingQueueOrders => PendingQueueOrdersCount > 0;

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

    /// <summary>Raised whenever <see cref="IsCustomerDisplayEnabled"/> changes, so the host
    /// (App.axaml.cs) can show or hide the customer-facing window it owns. Same decoupling
    /// role as <see cref="LogoutRequested"/>: this class states intent, the host performs
    /// the window mechanics.
    ///
    /// Subscribe via <see cref="SubscribeCustomerDisplayVisibility"/> rather than <c>+=</c>
    /// directly — see its remarks for why a bare subscription can silently never fire.</summary>
    public event EventHandler<bool>? CustomerDisplayVisibilityChanged;

    /// <summary>Subscribes <paramref name="handler"/> and immediately calls it with the
    /// flag's current value. Use this rather than <c>+=</c>: the event only fires on a
    /// *change*, and ICashFeatureService is a singleton that survives a logout/login cycle,
    /// so the flag may already hold its final value by the time a host subscribes and
    /// nothing would ever fire. Baking the initial call into the subscription is the only
    /// version of that rule a caller cannot forget.</summary>
    public void SubscribeCustomerDisplayVisibility(EventHandler<bool> handler)
    {
        CustomerDisplayVisibilityChanged += handler;
        handler(this, IsCustomerDisplayEnabled);
    }

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

        // Raise after parking, not before: the host's handler acts on the shared customer
        // window synchronously off the back of this call, and it must see the display view
        // model already idle rather than still showing the last cart.
        CustomerDisplayVisibilityChanged?.Invoke(this, value);
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

    // Exit menu: close the shift and leave / hand over to the next cashier / shut down
    [ObservableProperty] private bool _isExitMenuVisible = false;

    /// <summary>Set while a shift close started from the exit menu is in flight, so
    /// DoCloseShiftAsync knows to shut the app down once it succeeds. Owned by
    /// <see cref="BeginCloseShiftAsync"/> — see there for why it can't leak between requests.</summary>
    private bool _exitAfterShiftClose = false;

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

    /// <summary>Set when ShiftService reports HTTP 403 on a shift operation — the code this
    /// backend actually sends for a rejected session.
    ///
    /// Unlike <see cref="IsSessionRevoked"/> this is NOT permanent, and the difference is
    /// the whole point. A 401 means the token is dead and a dead token does not heal. A 403
    /// here is ambiguous: an expired JWT, a bad cash token, a tenant-database blip and
    /// several configuration faults all produce the identical body (see
    /// <see cref="VvCash.Services.Api.IShiftService.AccessDenied"/> for the full list), and
    /// only some of them mean the session is over. So this clears the moment a shift id
    /// actually comes back — see <see cref="OnCurrentShiftIdChanged"/> — rather than
    /// leaving a red warning over a register that has since opened its shift.
    ///
    /// Drives the explanation inside the shift modal rather than the top banner:
    /// PosView.axaml's Start Shift Modal Overlay is Grid.RowSpan="3" at ZIndex 1000 and
    /// covers that banner completely.</summary>
    [ObservableProperty] private bool _isShiftAccessDenied;

    /// <summary>A shift id in hand proves the server accepted this session after all, so a
    /// 403 raised earlier must stop showing. Hooked here rather than at the two assignment
    /// sites (InitializeAsync's GetShiftStateAsync and the OpenShift command) so neither can
    /// be added to later without the reset coming along. DoCloseShiftAsync assigns null on a
    /// successful close, which correctly does not clear anything.
    ///
    /// Note the trap for whoever adds background shift-state polling next (StartBackgroundSync
    /// already has a loop to hang it on): CommunityToolkit only invokes this hook when the new
    /// value actually differs from the old one, so a recheck that comes back with the *same*
    /// shift id we already hold — e.g. a plain `CurrentShiftId = await
    /// _shiftService.GetShiftStateAsync();` — will not fire it and will not clear a stale
    /// true here. Anything that re-reads shift state without necessarily changing it must
    /// clear <see cref="IsShiftAccessDenied"/> itself.</summary>
    partial void OnCurrentShiftIdChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value)) IsShiftAccessDenied = false;
    }

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

    /// <summary>Update badge and modal state. Injected rather than built here because it
    /// is a singleton: PosViewModel is transient, and an update found before the cashier
    /// visited returns must still be on screen when they come back.</summary>
    public UpdateViewModel Update { get; }

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
        // AddToCart/ResumeParkedSale below, so the permission it grants stays true for as
        // long as the overlay is up.
        //
        // Granted outright rather than from CanEndSellerSession. Withdrawing it on a
        // non-empty cart rested on one premise, stated where the rule was written: that
        // AddToCart's gate only re-asks on an EMPTY cart, so dropping the seller
        // mid-receipt would leave the rest of it with nobody confirmed and nothing to
        // re-prompt. That premise is gone — AddToCart no longer asks at all, and Pay()
        // refuses outright while the session is stale, so a receipt whose seller was
        // dropped mid-way is caught at the till and cannot be paid unattributed (see
        // SignOutMidReceipt_NextItemReAsks_AndPayStillRefuses).
        //
        // What the restriction did cost was the only control that does the job: the cart
        // is hardly ever empty at the moment somebody wants to stop selling, so in
        // practice the button was never there when it was needed. SellerSwitchViewModel
        // still hides it whenever there is nobody to sign out of (Current == null) and in
        // approval mode — see CanSignOut — so this grants permission, it does not force
        // the control on screen.
        SellerSwitchRequested?.Invoke(this, new SellerSwitchRequest(canSignOut: true));
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

    /// <summary>The header's own close-shift button. Always a plain close: whatever the exit
    /// menu may have asked for earlier is reset by <see cref="BeginCloseShiftAsync"/>, so an
    /// abandoned exit can never make this button close the app as a side effect.</summary>
    [RelayCommand]
    private Task CloseShift() => BeginCloseShiftAsync(exitAfterClose: false);

    /// <summary>Shared entry point for every way of closing the shift, carrying what should
    /// happen once it succeeds. The flag is written on every entry rather than only when set,
    /// which is what keeps it from leaking across requests — see <see cref="CloseShift"/>.
    ///
    /// It deliberately survives the two ways this call can suspend mid-flight (the supervisor
    /// approval overlay and the parked-sales confirm), because both resume *this* request:
    /// <see cref="OnCloseShiftApproved"/> and <see cref="ConfirmCloseShift"/> continue with it
    /// untouched, while <see cref="CancelCloseShift"/> — the one path that abandons the
    /// request outright — clears it.</summary>
    private async Task BeginCloseShiftAsync(bool exitAfterClose)
    {
        _exitAfterShiftClose = exitAfterClose;

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

        // The one path that abandons the request rather than resuming it, so the pending
        // "and then exit" intent dies with it — see BeginCloseShiftAsync.
        _exitAfterShiftClose = false;
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

            // Only when the close was started from the exit menu's "close the shift and
            // leave" branch (see ExitWithShiftClose). A close from the header button leaves
            // the register running on the start-shift modal exactly as before.
            if (_exitAfterShiftClose) CloseApplication();
        }

        // Cleared on both branches: on success the exit above already happened, and on
        // failure the shift is still open, so a later close must not inherit this intent.
        _exitAfterShiftClose = false;
    }

    [RelayCommand]
    private void CloseAlertModal()
    {
        IsAlertModalVisible = false;
    }

    private void CloseApplication()
    {
        // Set before the Close() call, not after: MainWindow's own Closing hook reads this to
        // tell a decided exit from the cashier hitting the window's X (or Alt+F4), and the
        // hook runs synchronously inside Close().
        IsExitConfirmed = true;

        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow?.Close();
        }
    }

    /// <summary>True once one of the exit menu's branches has decided the app really is going
    /// away, so <c>MainWindow.OnClosing</c> stops intercepting and lets the window close. Kept
    /// as plain state rather than an event because that hook needs to answer synchronously
    /// while it decides whether to cancel the close.</summary>
    public bool IsExitConfirmed { get; private set; }

    /// <summary>What the register's power button now does: ask what "exit" means to the
    /// cashier — end the shift, hand the register to someone else, or shut the app down —
    /// instead of closing the window outright and silently leaving the shift open.
    ///
    /// Also what MainWindow's Closing hook opens, so closing the window by its X asks the same
    /// question. Note this is *not* what the start-shift modal's own "exit application" button
    /// does: that one still runs <see cref="CloseApplicationCommand"/> directly, since it is
    /// already a screen of explicit choices and no shift can be open behind it.</summary>
    [RelayCommand]
    private void OpenExitMenu() => IsExitMenuVisible = true;

    [RelayCommand]
    private void CancelExit() => IsExitMenuVisible = false;

    /// <summary>"Close the shift and leave." Goes through the ordinary close path, so the
    /// CanCloseShift gate and the parked-sales confirm both still apply — the app is closed by
    /// DoCloseShiftAsync only once the close actually succeeds, and a refused approval or a
    /// failed request leaves the register running with its shift intact.</summary>
    [RelayCommand]
    private async Task ExitWithShiftClose()
    {
        IsExitMenuVisible = false;
        await BeginCloseShiftAsync(exitAfterClose: true);
    }

    /// <summary>"Hand the register over." Same sign-out as the shift modal's escape hatch: the
    /// shift stays open, only the session ends, and the next cashier logs in on top of it.</summary>
    [RelayCommand]
    private void ExitToLogin()
    {
        IsExitMenuVisible = false;
        PerformSignOut(string.Empty);
    }

    /// <summary>"Just close the program." Allowed with a shift still open — the menu says so in
    /// as many words (ExitShiftStaysOpen) — because the shift lives on the server and whoever
    /// opens the register next resumes it; refusing to close would only strand a cashier whose
    /// shift genuinely has to outlive this session.</summary>
    [RelayCommand]
    private void ExitApplication()
    {
        IsExitMenuVisible = false;
        CloseApplication();
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

    /// <summary>True when no receipt is in progress — i.e. the cart is empty. The
    /// condition <see cref="EndReceipt"/> guards on, named so that guard reads as the rule
    /// it is rather than as a bare collection check.
    ///
    /// No longer gates the seller-switch overlay's "stop selling" control. It used to, on
    /// the premise that AddToCart's gate only re-asks who is selling on an EMPTY cart, so
    /// dropping the seller mid-receipt would strand the rest of that receipt with nobody
    /// confirmed and nothing to re-prompt. AddToCart now re-asks on every add while nobody
    /// is confirmed, and <see cref="Pay"/> refuses without a seller outright, so that
    /// premise no longer holds and the restriction only withheld the control at the one
    /// moment it was wanted — see <see cref="OpenSellerSwitch"/>.
    ///
    /// EndReceipt's own use of it is unaffected by that change and stays: it is about not
    /// treating a returns/exchange dialog closing as the end of a receipt that is still
    /// being rung up, which has nothing to do with who may sign out.</summary>
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
    /// Guarded on <see cref="CanEndSellerSession"/>: the returns/exchange dialogs are
    /// separate windows that never touch the POS cart, so a dialog can close having booked
    /// a document while the current receipt is still mid-ring. Treating that as the end of
    /// a receipt would drop the seller out from under a sale still being rung up and make
    /// the cashier re-confirm mid-receipt for something that was never part of it. Pay and
    /// ClearCart both empty the cart themselves before calling this, so the guard is a
    /// no-op for them.</summary>
    private void EndReceipt()
    {
        if (!CanEndSellerSession) return;
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
        ICashFeatureService features,
        UpdateViewModel update,
        IQueueClient? queueClient = null,
        IQueueSettings? queueSettings = null)
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
        Update = update;
        _queueClient = queueClient;
        _queueSettings = queueSettings;

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
        // The one flag that does not get the benefit of the doubt. Every other flag here
        // reads as enabled until proven otherwise, which is right on a shop floor. This one
        // faces a paying customer: showing a display the store switched off is worse than
        // briefly showing nothing, so it stays hidden until the real map has actually
        // loaded (see ICashFeatureService.HasLoaded).
        IsCustomerDisplayEnabled = _features.HasLoaded && features.IsEnabled(CashFeatureCodes.CustomerDisplay);
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

            // Deliberately not MinValue: the first check waits a minute so it does not
            // compete with login and the first catalogue sync for the same connection.
            DateTime lastUpdateCheck = DateTime.Now - TimeSpan.FromMinutes(59);

            while (!token.IsCancellationRequested)
            {
                // Ping the server every 10 seconds to update IsSystemOnline status
                await _syncService.CheckSystemOnlineAsync();

                // Task 25: same 10-second tick keeps the outbox badge current. This is a
                // local SQLite count, not a network call — QueueFlushLoop (App.axaml.cs)
                // is what actually drains the buffer on its own 15-second timer; this only
                // re-reads what is left in it, so the cashier's badge does not lag behind
                // a flush that happened to land between two ticks here.
                if (_queueClient != null)
                {
                    var pendingCount = await _queueClient.PendingCountAsync();
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => PendingQueueOrdersCount = pendingCount);
                }

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

                // Once an hour is plenty: releases are cut by hand, and the register
                // stays on all day. CheckAsync never throws and marshals its own state
                // changes to the UI thread.
                if (DateTime.Now - lastUpdateCheck >= TimeSpan.FromHours(1))
                {
                    lastUpdateCheck = DateTime.Now;
                    await Update.CheckAsync(token);
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

        // Task 25: initial read of the queue outbox count. _queueClient has no
        // count-changed event to subscribe to (unlike _expenseDocumentService above) —
        // StartBackgroundSync's own loop keeps this current afterwards, on the same
        // 10-second cadence it already polls IsSystemOnline on.
        if (_queueClient != null)
        {
            PendingQueueOrdersCount = await _queueClient.PendingCountAsync();
        }

        _expenseDocumentService.SessionRevoked += OnSessionRevoked;
        _shiftService.SessionRevoked += OnShiftSessionRevoked;
        _shiftService.AccessDenied += OnShiftAccessDenied;

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

    /// <summary>Through the shared loader rather than fetching here, so the catalog grid,
    /// the cart and the exchange screen all read one cache: the same product shown on two
    /// screens costs one download, not one per screen.</summary>
    private Task LoadProductImageAsync(Product product)
        => ProductImageLoader.LoadIntoAsync(_httpClient, _settingsService.BackendUrl, product);

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

        // Category filtering above is in-memory and stays immediate. The catalog query
        // is debounced: it hits SQLite, and firing one per keystroke meant a five-letter
        // product name cost five queries, four of whose results were replaced before the
        // cashier could read them. Same 300ms and same supersede-by-CTS shape as the
        // quote debounce.
        _ = SearchDebouncedAsync();
    }

    private async Task SearchDebouncedAsync()
    {
        var previous = _searchCts;
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        previous?.Cancel();
        previous?.Dispose();
        try
        {
            await Task.Delay(300, cts.Token);
        }
        catch (TaskCanceledException)
        {
            return; // a later keystroke owns the search now
        }

        try
        {
            await LoadProductsAsync(SelectedCategory?.Id);
        }
        catch (Exception ex)
        {
            // Detached task, same as RequoteSafeAsync: a failed search must not vanish
            // silently nor take the register down.
            System.Diagnostics.Debug.WriteLine($"[PosViewModel] Search failed: {ex}");
        }
    }

    private void OnCartChanged(object? sender, EventArgs e)
    {
        CartItems = new ObservableCollection<CartItem>(_cartService.Items);
        RefreshPromoChip();

        // Gated on the empty -> non-empty transition, not on "the cart now holds exactly
        // one line". That older condition is true again after every +/- tap on a
        // single-line receipt, and again the moment a two-line receipt drops back to one
        // — so the number that is supposed to count receipts climbed while the cashier
        // adjusted a quantity.
        var hasItems = CartItems.Count > 0;
        if (hasItems && !_receiptOpen)
        {
            OrderNumber++;
            OrderDateTime = DateTime.Now.ToString("dd MMM, yyyy • HH:mm");
        }
        else if (!hasItems)
        {
            OrderDateTime = string.Empty;
        }
        _receiptOpen = hasItems;
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

        PushToCustomerDisplay();

        if (!_applyingQuoteResult)
            TriggerRequote();
    }

    /// <summary>Название последнего пробитого товара — верхняя строка кадра витрины.
    /// Живёт здесь, а не передаётся из AddToCart в отправку: изменение количества и
    /// скидка тоже меняют итог и обязаны перерисовать кадр, а названия у них своего
    /// нет.</summary>
    private string _displayedItemName = string.Empty;

    /// <summary>Один кадр на одно изменение корзины — и это главное, что здесь есть.
    ///
    /// Раньше отправок было две. AddToCart вызывал ShowItemAsync сам, а
    /// _cartService.AddProduct поднимал CartChanged синхронно, и OnCartChanged успевал
    /// поставить ShowTotalAsync в очередь раньше. Очередь в VfdDisplayService строго
    /// FIFO (порядок кадров там гарантирован намеренно, см. её собственные примечания),
    /// так что кадр товара всегда затирал итог: покупатель видел, что пробили, и не
    /// видел, сколько должен, — итог держался ровно одну отправку в порт, ~45мс на
    /// 9600 бод. Плюс лишнее открытие COM-порта с ESC @ на каждый скан, то есть
    /// видимое мигание табло.
    ///
    /// Теперь кадр один и несёт обе половины: строка 1 — товар, строка 2 — итог по
    /// чеку. Гонка исчезает не по счастливому порядку строк, а потому, что отправитель
    /// остался один.</summary>
    private void PushToCustomerDisplay()
    {
        // Пустая корзина — единственный момент, когда название можно и нужно забыть:
        // иначе оно пережило бы чек и следующий начался бы с чужого товара на витрине.
        if (!CartItems.Any())
            _displayedItemName = string.Empty;

        _ = string.IsNullOrEmpty(_displayedItemName)
            ? _customerDisplayService.ShowTotalAsync(TotalAmount)
            : _customerDisplayService.ShowItemAsync(_displayedItemName, TotalAmount);
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
        // Cancel, then dispose: a CancellationTokenSource holds the timer registration
        // its Task.Delay created, and one is superseded per cart change — several per
        // second while a receipt is being rung up.
        ReplaceQuoteCts(out var cts);
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
            ReplaceQuoteCts(out var cts);
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

    /// <summary>Cancels and disposes whatever quote request was in flight, and installs a
    /// fresh source as the current one. The dispose is the point: the superseded source
    /// still holds the timer registration behind its Task.Delay, and one is superseded per
    /// cart change — several a second while a receipt is being rung up.</summary>
    private void ReplaceQuoteCts(out System.Threading.CancellationTokenSource cts)
    {
        var previous = _quoteCts;
        cts = new System.Threading.CancellationTokenSource();
        _quoteCts = cts;
        previous?.Cancel();
        previous?.Dispose();
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

    /// <summary>Reaction to a 403 on a shift operation. Deliberately does everything
    /// <see cref="OnShiftSessionRevoked"/> does not: no sign-out, no navigation, no touching
    /// of credentials. Most of the things that make this backend answer 403 say nothing
    /// about the session (see <see cref="VvCash.Services.Api.IShiftService.AccessDenied"/>),
    /// so the register explains itself and leaves the decision to the cashier, who already
    /// has a sign-out button on the very modal this message appears in. Marshals to the UI
    /// thread for the same reason every other handler here does — ShiftService posts the
    /// event rather than invoking it inline.</summary>
    private void OnShiftAccessDenied(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsShiftAccessDenied = true);
    }

    private void OnParkedSaleCountChanged(object? sender, int count)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ParkedSalesCount = count);
    }

    private void OnSyncStatusChanged(object? sender, bool isOnline)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSystemOnline = isOnline);
    }

    // Not async void. This is raised from the background sync loop, so an exception out
    // of it — a SQLite read that fails, a catalog load that throws — has no caller to
    // land in and takes the process down with it. Fire the work off as a task with its
    // own catch instead, the same shape RequoteSafeAsync uses.
    private void OnProductsSynced(object? sender, EventArgs e) => _ = OnProductsSyncedAsync();

    private async Task OnProductsSyncedAsync()
    {
        try
        {
            // The sync that just finished also refreshed the promotion cache in SQLite.
            await _promotionProvider.RefreshAsync();

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await LoadCategoriesAsync();
                await LoadProductsAsync(SelectedCategory?.Id);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PosViewModel] Post-sync refresh failed: {ex}");
        }
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
        _shiftService.AccessDenied -= OnShiftAccessDenied;
        _parkedSaleService.CountChanged -= OnParkedSaleCountChanged;
        _syncService.SyncStatusChanged -= OnSyncStatusChanged;
        _syncService.ProductsSynced -= OnProductsSynced;
        _sellerSession.CurrentChanged -= OnSellerChanged;

        // This class is the publisher of this one, not a subscriber, so there is nothing to
        // unsubscribe from — but the instance is not collected when it is discarded either:
        // PosViewModel is resolved from the root provider, which captures every IDisposable
        // it constructs for its own eventual disposal (see App.axaml.cs's remarks where
        // SellerSwitchViewModel deliberately avoids exactly that). Combined with
        // InitializeAsync being fire-and-forget with no cancellation, a discarded instance
        // can still reach ApplyFeatures and fire this event long after its screen is gone —
        // at a host handler that drives the one long-lived customer window the *current*
        // session is using. Dropping the invocation list makes that a guaranteed no-op.
        CustomerDisplayVisibilityChanged = null;

        _syncCancellationTokenSource?.Cancel();
        _syncCancellationTokenSource?.Dispose();
        _syncCancellationTokenSource = null;

        // Cancel any in-flight debounced requote so it can't mutate a disposed VM.
        _quoteCts?.Cancel();
        _quoteCts?.Dispose();
        _quoteCts = null;

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
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
        // Deliberately does NOT ask who is selling. Building a receipt is not the moment
        // that needs an answer — taking money for it is, and that is the one gate left
        // (see Pay). Asking here meant the overlay covered the screen on the very first
        // product of every receipt, which is the busiest moment at the till and the one
        // where a cashier is least inclined to read it; and because the ask could not
        // block this method, the product landed in the cart whether or not anyone
        // answered, which is how a dismissed ask used to leave the register in a state
        // with no way back to the question.
        //
        // Nothing is lost by waiting: Pay refuses outright while the session is stale, and
        // the answer given there resumes the payment by itself.

        // Any add is genuine register activity — resets the idle timer, so a receipt that
        // is actively being rung up does not go stale under the cashier and make Pay ask
        // at the checkout for a session that never lapsed.
        _sellerSession.Touch();

        // Порядок обязателен. AddProduct поднимает CartChanged синхронно, кадр витрины
        // собирается уже внутри него (OnCartChanged -> PushToCustomerDisplay) — значит
        // название должно лежать на месте до вызова, а не после. Присвоение строкой
        // ниже опоздало бы ровно на один скан: покупатель видел бы предыдущий товар.
        _displayedItemName = product.Name;
        _cartService.AddProduct(product);
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
    /// gates when a cap is actually set.
    ///
    /// Takes a percent because the cap is one. An amount-mode discount is converted by
    /// <see cref="DiscountAsPercent"/> before it gets here rather than being waved
    /// through: the cap existed to bound how much of a receipt one seller can give away,
    /// and "500 off" gives away exactly as much as "50%" does on a 1000 receipt. Leaving
    /// amount mode out of the check turned the mode toggle into a way around it.</summary>
    private bool NeedsDiscountApproval(decimal percent)
    {
        var cap = _sellerSession.Current?.MaxDiscount ?? 0m;
        return cap > 0m && percent > cap;
    }

    /// <summary>What the entered discount comes to as a percent of the current receipt,
    /// or null when that cannot be established — an amount typed against an empty cart
    /// has no subtotal to be a percent of.</summary>
    private decimal? DiscountAsPercent(decimal value)
    {
        if (IsDiscountPercentMode) return value;
        var subtotal = _cartService.Subtotal;
        return subtotal > 0m ? value / subtotal * 100m : null;
    }

    private void RefuseDiscount(string reason)
    {
        CloseDiscountModal();
        AlertMessage = reason;
        IsAlertModalVisible = true;
    }

    [RelayCommand]
    private void ApplyManualDiscount()
    {
        if (!decimal.TryParse(DiscountInputValue, out var value))
        {
            CloseDiscountModal();
            return;
        }

        // Bounds first, before the cap check has anything to reason about. The pad
        // accepts any number of digits and CartService clamps the resulting discount to
        // the subtotal, so "500" in percent mode looked like nothing was wrong — it just
        // produced a receipt for zero. A discount that takes more than the receipt is
        // worth is an entry error every time, not a decision to escalate.
        if (value <= 0m)
        {
            RefuseDiscount("Скидка должна быть больше нуля.");
            return;
        }

        var percent = DiscountAsPercent(value);
        if (percent == null)
        {
            RefuseDiscount("Скидка суммой недоступна: в чеке нет товаров.");
            return;
        }
        if (percent > 100m)
        {
            RefuseDiscount(IsDiscountPercentMode
                ? "Скидка не может превышать 100%."
                : "Скидка больше суммы чека.");
            return;
        }

        // Same seller-switch-off exception as CloseShift/OpenReturns: with no separate
        // sellers there is nobody else's approval to escalate to, and the overlay that
        // would collect it is hidden along with the flag.
        //
        // The escalation carries the percent even when the cashier typed an amount: it
        // is what the approver's own cap is compared against, and what
        // ApplyApprovedDiscount applies. On the receipt in front of them the two are the
        // same money; should the cart change afterwards, a percent is the safer of the
        // two to have approved.
        if (IsSellerSwitchEnabled && NeedsDiscountApproval(percent.Value))
        {
            CloseDiscountModal();
            DiscountApprovalRequested?.Invoke(this, percent.Value);
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
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) return;

        var mainWindow = desktop.MainWindow;
        if (mainWindow == null) return;

        var dialog = new VvCash.Views.CustomerSearchWindow();
        dialog.DataContext = new CustomerSearchViewModel(
            _counterpartyService,
            IsCustomerRegistrationEnabled,
            result => dialog.Close(result),
            // Владелец — окно поиска, а не главное окно: если кассир отменит
            // регистрацию, он вернётся в поиск с целым запросом и списком.
            query => ShowCustomerRegistrationAsync(dialog, query));

        var selected = await dialog.ShowDialog<object>(mainWindow) as CounterpartyResponse;
        if (selected != null)
        {
            ApplySelectedCustomer(selected);
        }
    }

    private async Task OpenCustomerRegistration()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) return;

        var mainWindow = desktop.MainWindow;
        if (mainWindow == null) return;

        var created = await ShowCustomerRegistrationAsync(mainWindow, string.Empty);
        if (created != null)
        {
            ApplySelectedCustomer(created);
        }
    }

    /// <summary>Единственное место, где открывается окно регистрации. Оба входа —
    /// кнопка в тулбаре и «Создать клиента» из окна поиска — отличаются только
    /// владельцем окна и наличием строки для префилла.</summary>
    private async Task<CounterpartyResponse?> ShowCustomerRegistrationAsync(Avalonia.Controls.Window owner, string searchQuery)
    {
        var dialog = new VvCash.Views.CustomerRegistrationWindow();
        var vm = new CustomerRegistrationViewModel(result => dialog.Close(result), _counterpartyService, _settingsService);
        // Формат спрашиваем у самой view model, а не разрешаем второй раз здесь:
        // два независимых Resolve согласованы только по совпадению, а разойдясь —
        // дали бы форму с номером, который нельзя ни дописать, ни сохранить.
        vm.ApplyPrefill(CustomerPrefill.FromSearchQuery(searchQuery, vm.PhoneDigitCount));
        dialog.DataContext = vm;

        // as, а не каст: окно закрывается либо созданным клиентом, либо null, но
        // ошибиться здесь означало бы уронить кассу на InvalidCastException.
        // Тот же приём уже применён в OpenParkedSales.
        return await dialog.ShowDialog<object>(owner) as CounterpartyResponse;
    }

    /// <summary>Клиент выбран — неважно, найден в базе или только что создан.
    /// Отдельный метод, а не копия в двух местах: правило «применить карту
    /// скидки и пересчитать корзину» должно жить в одном месте, иначе следующий
    /// вход в выбор клиента просто забудет про requote, и расхождение будет
    /// молчаливым — ровно так тулбарная кнопка регистрации и теряла клиента.</summary>
    private void ApplySelectedCustomer(CounterpartyResponse customer)
    {
        SelectedCustomer = customer;
        if (customer.DiscountCard != null && customer.DiscountCard.Discount > 0)
        {
            _cartService.SetCustomerDiscount(customer.DiscountCard.Discount); // offline fallback
            StatusMessage = $"Клиент: {customer.FullName} • Скидка по карте: {customer.DiscountCard.Discount}%";
        }
        else
        {
            _cartService.ClearCustomerDiscount();
            StatusMessage = $"Выбран клиент: {customer.FullName}";
        }
        TriggerRequote();
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
                var returnsVm = new ReturnsViewModel(dialog, _returnService, _printerService, _settingsService, _features,
                    _cashOperationService, _counterpartyService, _session.CashId);
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
        // Carries a continuation, same as Pay's gate: this press does not open the exchange
        // window, but the answer to the question does, so the cashier never has to work out
        // that Exchange needs tapping a second time. Resumes ShowExchangeDialogAsync
        // directly rather than re-entering this method, so a switch that somehow left the
        // session stale cannot bounce back into the gate in a loop.
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
            SellerSwitchRequested?.Invoke(this, new SellerSwitchRequest(
                canSignOut: false,
                onSwitched: _ => ShowExchangeDialogAsync()));
            return;
        }

        await ShowExchangeDialogAsync();
    }

    /// <summary>Opens the exchange window itself, once the seller gate above is satisfied.
    /// Split out for the same reason <see cref="ProceedToPayAsync"/> is: the gate needs
    /// something to resume that cannot re-enter the gate.</summary>
    private async Task ShowExchangeDialogAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                var dialog = new VvCash.Views.ExchangeWindow();
                var exchangeVm = new ExchangeViewModel(
                    dialog, _returnService, _cashOperationService, _expenseDocumentService,
                    _counterpartyService, _settingsService, _productService, _syncService,
                    _printerService, _features, _quoteService, _httpClient,
                    _promotionProvider.MoneyPolicy, CurrentShiftId ?? string.Empty,
                    _sellerSession.Current?.Id, _session.CashId, _session.WarehouseId, IsSystemOnline);
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
    /// <see cref="AddToCart"/>. That used to matter a great deal: it meant a resumed
    /// receipt slipped past AddToCart's seller gate, so this method carried a copy of it.
    /// Both are gone — <see cref="Pay"/> is the only place the register asks who is
    /// selling, and a resumed receipt reaches it exactly like any other, so where the
    /// items came from stops being a question about attribution at all.
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

        // No seller gate here either, for the same reason AddToCart no longer has one:
        // pulling a parked receipt back up is not the moment that needs an answer. Pay is,
        // and a resumed receipt reaches it exactly like any other — the fact that these
        // items never went through AddToCart, which is why this gate existed at all, stops
        // mattering once the question is asked at the till rather than at the catalog.

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

        // The one place the register asks who is selling. Taking money is the moment that
        // actually needs the answer — it is the last point where refusing is still free,
        // and the only one where the cashier is guaranteed to be looking at the screen.
        // Everything upstream (AddToCart, resuming a parked receipt) deliberately does not
        // ask; see AddToCart for why asking there was worse than useless.
        //
        // Gates on IsStale, not merely Current == null. Being the only gate left, this one
        // carries the idle timeout as well: a seller who confirmed, then walked away long
        // enough to lapse, must not stay signed under a receipt the next person rang up.
        // Touch() runs on every add, so a receipt actively being rung up never lapses under
        // the cashier — reaching this stale means the register genuinely sat idle.
        //
        // OnSwitched is what stops the refusal being a dead press: once the PIN is in, the
        // payment resumes by itself rather than making the cashier press Pay a second time.
        // It calls ProceedToPayAsync directly, never the command, so a switch that somehow
        // left the session still stale cannot bounce back into this gate in a loop.
        //
        // canSignOut is a hard false: CartItems is non-empty by the guard at the top of
        // this method, so there is nothing to sign out of — see SellerSwitchRequest.
        //
        // With seller switching off this register has no separate identity to confirm
        // (everything is the shift owner's), nobody ever becomes Current, and the gate
        // would refuse every payment forever — so it degrades to a no-op, same exception
        // CloseShift and OpenReturns make.
        if (IsSellerSwitchEnabled && _sellerSession.IsStale)
        {
            SellerSwitchRequested?.Invoke(this, new SellerSwitchRequest(
                canSignOut: false,
                onSwitched: _ => ProceedToPayAsync()));
            return;
        }

        await ProceedToPayAsync();
    }

    /// <summary>Everything Pay does once it is allowed to: quote the cart, then hand the
    /// payment screen to the host. Split out so the seller gate above has something to
    /// resume that cannot re-enter the gate itself.
    ///
    /// Re-checks the cart rather than trusting Pay's own guard: when this runs as the
    /// gate's continuation, an overlay stood between the two, and a method that books a
    /// document should not assume what was true before it.</summary>
    private async Task ProceedToPayAsync()
    {
        if (!CartItems.Any()) return;

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
                        Counterparty = SelectedCustomer?.Id,
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
                    // Detailed, not the bool overload: a document the server refused on
                    // its merits will be refused identically on every retry, so "please
                    // try again" is the one instruction that cannot help. The server's
                    // own reason is what tells the cashier whether to fix the receipt or
                    // fetch a manager.
                    var outcome = await _expenseDocumentService.CreateExpenseDocumentDetailedAsync(request);

                    if (outcome.Posted || outcome.Queued)
                    {
                        // The document number is empty for a sale that was queued rather
                        // than posted — it has no number until it syncs — and the seller
                        // is absent on a register with switching off. The receipt prints
                        // neither line in those cases rather than an empty label.
                        await _printerService.PrintReceiptAsync(
                            _cartService.Items,
                            Subtotal, TotalDiscount, TotalAmount,
                            _cartService.AppliedCoupons,
                            _cartService.AppliedDiscountName,
                            documentNumber: outcome.DocumentNumber,
                            warehouseName: null,
                            sellerName: _sellerSession.Current?.FullName,
                            saleDate: DateTime.Now.ToString("dd.MM.yyyy HH:mm"));

                        // Task 22 (Important 3/9 review round): постановка в очередь и
                        // печать талона/бегунка — два разных вопроса, которые раньше
                        // отвечал один and-less булев флаг, и это было неверно с обеих
                        // сторон:
                        //
                        //  - QueueRole.Off + талонный принтер — конфигурация, которую
                        //    комментарии в этом же коде называют половиной парка — со
                        //    старым флагом ставила заказ в исходящий буфер на каждой
                        //    продаже. Транспорт отвечал Unreachable (адрес сервера пуст),
                        //    а QueueFlushLoop при Off вообще не запускается (см.
                        //    App.axaml.cs) — значок недоставленного рос на единицу с
                        //    каждой продажей и не убывал никогда.
                        //  - Кухонный экран без кухонного принтера — конфигурация, которую
                        //    спека называет явно, — со старым флагом не заводила заказ
                        //    вовсе: ни один принтер не держит Ticket/KitchenOrder, значит
                        //    флаг был false, и экран кухни молча пустовал.
                        //
                        // Отсюда два разных условия. Номер нужен, когда есть на чём его
                        // напечатать (талонный или кухонный принтер) ИЛИ когда сетевая
                        // очередь включена (табло/KDS должны увидеть заказ, даже если на
                        // самой кассе печатать его нечем). В буфер и на сервер заказ уходит
                        // ТОЛЬКО когда очередь включена — печать по отдельному номеру
                        // (IssueNumberAsync) не создаёт заказ и не трогает буфер, потому что
                        // при Off его всё равно некому доставить и некому вычистить оттуда.
                        var queueRoleOn = _queueSettings != null && _queueSettings.QueueRole != QueueRole.Off;
                        var hasPrinterForQueueNumber = _settingsService.Printers.Any(p => p.IsEnabled
                            && (p.Roles.HasFlag(PrintRole.Ticket) || p.Roles.HasFlag(PrintRole.KitchenOrder)));
                        var needsQueueNumber = hasPrinterForQueueNumber || queueRoleOn;

                        if (needsQueueNumber && _queueClient is not null)
                        {
                            // Строго после PrintReceiptAsync и строго до ClearCart():
                            // бегунку нужны строки корзины, а после ClearCart() их уже нет.
                            // Копия (.ToList()), а не живой _cartService.Items, по той же
                            // причине — не держать ссылку на список, который ClearCart()
                            // ниже опустошит.
                            var queueSale = new SaleReceiptData(
                                _cartService.Items.ToList(),
                                Subtotal, TotalDiscount, TotalAmount,
                                _cartService.AppliedDiscountName,
                                outcome.DocumentNumber,
                                null,
                                _sellerSession.Current?.FullName,
                                DateTime.Now.ToString("dd.MM.yyyy HH:mm"));

                            int? queueNumberValue;
                            string queueNumberTime;
                            if (queueRoleOn)
                            {
                                // Очередь включена: заказ должен доехать до сервера, чтобы
                                // его увидели KDS и табло — даже если на этой кассе печатать
                                // талон/бегунок нечем (тот самый случай "кухонный экран без
                                // кухонного принтера").
                                var queueOrder = await _queueClient.EnqueueAsync(queueSale);
                                queueNumberValue = queueOrder?.Number;
                                queueNumberTime = queueOrder?.CreatedAt.ToString("HH:mm") ?? DateTime.Now.ToString("HH:mm");
                            }
                            else
                            {
                                // Очередь выключена: серверу этот заказ показать некому, так
                                // что не создаём его вовсе — только номер для бумаги.
                                queueNumberValue = await _queueClient.IssueNumberAsync();
                                queueNumberTime = DateTime.Now.ToString("HH:mm");
                            }

                            // Null только тогда, когда не удалось получить сам номер (см.
                            // IQueueClient docstring) — печатать талон и бегунок тогда
                            // нечем: талон без номера хуже, чем его отсутствие, а бегунок
                            // без номера теряет смысл, ради которого его вообще заводили.
                            // Чек клиенту уже напечатан строкой выше и от этого никак не
                            // зависит. Принтеры сами отфильтруют себя по роли
                            // (CompositePrinterService), так что вызывать их безопасно и
                            // тогда, когда ни один принтер не держит Ticket/KitchenOrder —
                            // именно так заказ доезжает до KDS без бумаги на этой кассе.
                            if (queueNumberValue != null)
                            {
                                var queueNumber = queueNumberValue.Value.ToString(CultureInfo.InvariantCulture);
                                await _printerService.PrintTicketAsync(
                                    queueNumber,
                                    time: queueNumberTime,
                                    warehouseName: null);
                                await _printerService.PrintKitchenOrderAsync(queueSale, queueNumber);
                            }

                            // Immediate feedback rather than waiting for the next 10-second
                            // background tick (see StartBackgroundSync): EnqueueAsync above
                            // just wrote to (and may have already drained) the outbox, and
                            // this runs on the UI thread already, so there is no dispatcher
                            // hop needed the way that background poll requires. Safe to call
                            // even on the Off/IssueNumberAsync branch above, which never
                            // touches the outbox — it will simply read back whatever was
                            // already there.
                            PendingQueueOrdersCount = await _queueClient.PendingCountAsync();
                        }

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
                            AlertMessage = string.IsNullOrWhiteSpace(outcome.RejectionReason)
                                ? "Не удалось создать документ продажи. Повторите попытку."
                                : $"Сервер отклонил продажу: {outcome.RejectionReason}. "
                                  + "Повтор не поможет — исправьте чек или обратитесь к администратору.";
                            IsAlertModalVisible = true;
                            StatusMessage = "Payment failed.";
                        });
                    }
                }

                // Return to POS View
                NavigationRequest(this);
            }, IsMixedPaymentEnabled, hasCustomer: SelectedCustomer != null,
               creditTerms: SelectedCustomer is { } c
                   ? new MixedPaymentViewModel.CreditTerms(c.CreditLimit ?? 0m, c.CurrentBalance ?? 0m)
                   : null);

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
