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
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using VvCash.Services.Data;
using VvCash.Services.Hardware;

namespace VvCash.ViewModels;

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
    private readonly IQuoteService _quoteService;
    private readonly ISessionContext _session;
    private readonly HttpClient _httpClient;
    private readonly ISellerSession _sellerSession;
    private readonly ISellerRosterService _rosterService;
    private readonly IAuthService _authService;
    private CancellationTokenSource? _syncCancellationTokenSource;
    private System.Threading.CancellationTokenSource? _quoteCts;
    private bool _applyingQuoteResult;
    private string? _activePromoCode;

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
    private int _parkedSalesCount;
    public bool HasParkedSales => ParkedSalesCount > 0;

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
    public int CartItemsCount => CartItems.Sum(i => i.Quantity);
    public bool HasCartItems => CartItems.Count > 0;
    partial void OnCartItemsChanged(ObservableCollection<CartItem> value)
    {
        OnPropertyChanged(nameof(CartItemsCount));
        OnPropertyChanged(nameof(HasCartItems));
    }

    public bool HasTotalDiscount => TotalDiscount > 0;
    partial void OnTotalDiscountChanged(decimal value)
        => OnPropertyChanged(nameof(HasTotalDiscount));

    public bool HasProducts => Products.Count > 0;
    public bool ShowCatalogEmptyState => !IsViewingCategories && !HasProducts;
    partial void OnProductsChanged(ObservableCollection<Product> value)
    {
        OnPropertyChanged(nameof(HasProducts));
        OnPropertyChanged(nameof(ShowCatalogEmptyState));
    }
    partial void OnIsViewingCategoriesChanged(bool value)
        => OnPropertyChanged(nameof(ShowCatalogEmptyState));

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
    private bool _isSystemOnline = true;

    public string SystemStatusText => IsSystemOnline ? "SYSTEM ONLINE" : "SYSTEM OFFLINE";


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
    /// receipt (see <see cref="AddToCart"/>) or because the cashier tapped the seller chip
    /// (see <see cref="OpenSellerSwitch"/>). Plays the same decoupling role as
    /// <see cref="NavigationRequest"/> and <see cref="CustomerDisplayViewModel"/> —
    /// PosViewModel raises intent without knowing how it's fulfilled — but unlike those two
    /// settable delegate/property members, this is a genuine event: the host subscribes to
    /// it rather than being handed a callback to invoke.</summary>
    public event EventHandler? SellerSwitchRequested;

    /// <summary>Raised to ask the host (App.axaml.cs) to open the seller-switch
    /// overlay in approval mode (see <see cref="SellerSwitchViewModel.OpenForApproval"/>)
    /// because the current seller lacks <c>CanCloseShift</c> — see <see cref="CloseShift"/>.
    /// Plays the same decoupling role as <see cref="SellerSwitchRequested"/>: PosViewModel
    /// raises intent without knowing how the overlay gets opened.</summary>
    public event EventHandler? CloseShiftApprovalRequested;

    /// <summary>Current seller's name for the header chip, or — when none is selected — the
    /// same action-shaped invitation ("Who is selling?") already used by this button's
    /// tooltip and by the overlay's own heading, so an empty chip reads as something to
    /// press rather than as a caption. Recomputed whenever
    /// <see cref="ISellerSession.CurrentChanged"/> fires (see <see cref="OnSellerChanged"/>).</summary>
    public string SellerChipText => _sellerSession.Current?.FullName ?? I18nService.Instance["SelectSeller"];

    private void OnSellerChanged(object? sender, EventArgs e)
        => OnPropertyChanged(nameof(SellerChipText));

    [RelayCommand]
    private void OpenSellerSwitch() => SellerSwitchRequested?.Invoke(this, EventArgs.Empty);

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
        if (!(_sellerSession.Current?.CanCloseShift ?? false))
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
        IQuoteService quoteService,
        ISessionContext session,
        HttpClient httpClient,
        ISellerSession sellerSession,
        ISellerRosterService rosterService,
        IAuthService authService)
    {
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
        _quoteService = quoteService;
        _session = session;
        _httpClient = httpClient;
        _sellerSession = sellerSession;
        _rosterService = rosterService;
        _authService = authService;

        OpenCustomerRegistrationCommand = new AsyncRelayCommand(OpenCustomerRegistration);
        CloseApplicationCommand = new RelayCommand(CloseApplication);

        _cartService.CartChanged += OnCartChanged;
        _printerService.StatusChanged += OnPrinterStatusChanged;
        _sellerSession.CurrentChanged += OnSellerChanged;

        _ = InitializeAsync();
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

        _expenseDocumentService.UnsyncedDocumentsCountChanged += OnUnsyncedDocumentsCountChanged;
        UnsyncedDocumentsCount = await _expenseDocumentService.GetUnsyncedDocumentsCountAsync();

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

        if (CustomerDisplayViewModel != null)
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

    // Triggers all originate on the UI thread and the codebase never uses
    // ConfigureAwait(false), so these continuations resume on the UI thread —
    // required because applying a quote raises CartChanged, which rebuilds
    // UI-bound collections.
    private async Task RequoteAsync(System.Threading.CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var cardId = SelectedCustomer?.DiscountCard?.Identifier;
        var hasInput = !string.IsNullOrWhiteSpace(cardId) || !string.IsNullOrWhiteSpace(_activePromoCode);

        if (!IsSystemOnline || !hasInput || _cartService.Items.Count == 0 || string.IsNullOrWhiteSpace(_session.WarehouseId))
        {
            if (IsCurrentQuote(cts)) ApplyQuoteGuarded(() => _cartService.ClearQuote());
            return;
        }

        var request = QuoteRequestBuilder.Build(_cartService.Items, _session.WarehouseId!, cardId, _activePromoCode);
        var result = await _quoteService.QuoteAsync(request, ct);
        if (!IsCurrentQuote(cts)) return; // a newer requote superseded this one

        if (result == null)
        {
            // Network failure / offline: fall back to flat %.
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
        if (!_cartService.Items.Any() && _sellerSession.IsStale)
            SellerSwitchRequested?.Invoke(this, EventArgs.Empty);

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
        _ = _customerDisplayService.ClearAsync();
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
        DiscountInputValue = string.Empty;
        IsDiscountModalVisible = true;
    }

    [RelayCommand]
    private void CloseDiscountModal()
    {
        IsDiscountModalVisible = false;
    }

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

    [RelayCommand]
    private void ApplyManualDiscount()
    {
        if (decimal.TryParse(DiscountInputValue, out var value))
        {
            if (IsDiscountPercentMode)
            {
                _cartService.SetManualDiscount(value, 0);
            }
            else
            {
                _cartService.SetManualDiscount(0, value);
            }
        }
        CloseDiscountModal();
    }

    [RelayCommand]
    private void ClearManualDiscount()
    {
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
            _cartService.AppliedCoupons);
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
            .Select(i => new ParkedCartItem { Product = i.Product, Quantity = i.Quantity })
            .ToList(),
        ManualDiscountPercent = _cartService.ManualDiscountPercent,
        ManualDiscountAmount = _cartService.ManualDiscountAmount,
        CustomerDiscountPercent = _cartService.CustomerDiscountPercent,
        AppliedCoupons = _cartService.AppliedCoupons.ToList(),
        Customer = SelectedCustomer,
        Label = label
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
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                var dialog = new VvCash.Views.ReturnsWindow();
                dialog.DataContext = new ReturnsViewModel(dialog, _returnService, _printerService, _settingsService);
                await dialog.ShowDialog(mainWindow);
            }
        }
    }

    private async Task ResumeParkedSale(string id)
    {
        // Если в корзине уже есть товары — авто-отложить текущую продажу.
        if (_cartService.Items.Any())
        {
            await _parkedSaleService.ParkAsync(BuildSnapshot(null), TotalAmount);
            _cartService.ClearCart();
            _cartService.ClearCustomerDiscount();
            SelectedCustomer = null;
            ClearActivePromo();
        }

        var snapshot = await _parkedSaleService.ResumeAsync(id);
        if (snapshot == null) return;

        var items = snapshot.Items
            .Select(i => new CartItem { Product = i.Product, Quantity = i.Quantity })
            .ToList();

        // Set the customer before LoadSnapshot so the CartChanged cascade sees
        // the restored customer (matches the normal customer-select flow).
        SelectedCustomer = snapshot.Customer;

        _cartService.LoadSnapshot(
            items,
            snapshot.ManualDiscountPercent, snapshot.ManualDiscountAmount,
            snapshot.CustomerDiscountPercent,
            snapshot.AppliedCoupons);

        TriggerRequote();

        StatusMessage = "Отложенный чек возвращён.";
    }

    [RelayCommand]
    private void Pay()
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
            var mixedPaymentVm = new MixedPaymentViewModel(TotalAmount, async (result, cashAmount, cardAmount) =>
            {
                if (result)
                {
                    var request = new DocumentRequest
                    {
                        DocumentHash = Guid.NewGuid().ToString(),
                        SellerId = _sellerSession.Current?.Id,
                        ShiftId = CurrentShiftId,
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
                        Products = _cartService.Items.Select(item =>
                        {
                            var (pct, before) = QuoteLineResolver.Resolve(_cartService.Quote, item);
                            return new DocumentProduct
                            {
                                Name = item.Product.Name,
                                ProductId = item.Product.Id,
                                Quantity = item.Quantity,
                                SellPrice = item.Product.Price,
                                PriceBeforeDiscount = before,
                                DiscountPercent = pct
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
                            _cartService.AppliedCoupons);
                        _cartService.ClearCart();
                        _cartService.ClearCustomerDiscount();
                        SelectedCustomer = null;
                        ClearActivePromo();
                        StatusMessage = "Payment processed. Thank you!";

                        if (CustomerDisplayViewModel != null)
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
            });

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
