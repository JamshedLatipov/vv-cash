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
    private readonly IDiscountService _discountService;
    private readonly IPrinterService _printerService;
    private readonly ICustomerDisplayService _customerDisplayService;
    private readonly IShiftService _shiftService;
    private readonly IOfflineStorageService _offlineStorageService;
    private readonly ISyncService _syncService;
    private readonly ISettingsService _settingsService;
    private readonly IExpenseDocumentService _expenseDocumentService;
    private readonly ICounterpartyService _counterpartyService;
    private readonly IParkedSaleService _parkedSaleService;
    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _syncCancellationTokenSource;

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
    }

    [ObservableProperty] private decimal _totalDiscount;
    [ObservableProperty] private decimal _totalAmount;
    [ObservableProperty] private string _printerStatusText = "Printer Ready";
    [ObservableProperty] private bool _isPrinterReady = true;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isCatalogOpen = false;
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
        }
    }

    [RelayCommand]
    private async Task CloseShift()
    {
        if (string.IsNullOrEmpty(CurrentShiftId)) return;

        if (ParkedSalesCount > 0)
        {
            IsShiftCloseConfirmVisible = true;
            return;
        }

        await DoCloseShiftAsync();
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
        StatusMessage = "Reinitialization complete. Catalog updated.";
        await LoadCategoriesAsync();
        await LoadProductsAsync(SelectedCategory?.Id);
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
        IDiscountService discountService,
        IPrinterService printerService,
        ICustomerDisplayService customerDisplayService,
        IShiftService shiftService,
        IOfflineStorageService offlineStorageService,
        ISyncService syncService,
        ISettingsService settingsService,
        IExpenseDocumentService expenseDocumentService,
        ICounterpartyService counterpartyService,
        IParkedSaleService parkedSaleService,
        HttpClient httpClient)
    {
        _productService = productService;
        _categoryService = categoryService;
        _cartService = cartService;
        _discountService = discountService;
        _printerService = printerService;
        _customerDisplayService = customerDisplayService;
        _shiftService = shiftService;
        _offlineStorageService = offlineStorageService;
        _syncService = syncService;
        _settingsService = settingsService;
        _expenseDocumentService = expenseDocumentService;
        _counterpartyService = counterpartyService;
        _parkedSaleService = parkedSaleService;
        _httpClient = httpClient;

        OpenCustomerRegistrationCommand = new AsyncRelayCommand(OpenCustomerRegistration);
        CloseApplicationCommand = new RelayCommand(CloseApplication);

        _cartService.CartChanged += OnCartChanged;
        _printerService.StatusChanged += OnPrinterStatusChanged;

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
        if (!string.IsNullOrWhiteSpace(value) && !IsCatalogOpen)
        {
            IsCatalogOpen = true;
        }

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
        AppliedCoupons = new ObservableCollection<Coupon>(_cartService.AppliedCoupons);
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

        _syncCancellationTokenSource?.Cancel();
        _syncCancellationTokenSource?.Dispose();
        _syncCancellationTokenSource = null;
    }

    [RelayCommand]
    private async Task SearchProducts()
    {
        IsCatalogOpen = true;
        await LoadProductsAsync(SelectedCategory?.Id);
    }

    [RelayCommand]
    private async Task SelectCategory(Category? category)
    {
        SearchQuery = string.Empty;
        IsCatalogOpen = true;

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
    private void CloseCatalog()
    {
        IsCatalogOpen = false;
        SearchQuery = string.Empty;
    }

    [RelayCommand]
    private void AddToCart(Product product)
    {
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
        _ = _customerDisplayService.ClearAsync();
    }

    [RelayCommand]
    private async Task ApplyCoupon()
    {
        if (string.IsNullOrWhiteSpace(CouponCode)) return;
        var coupon = await _discountService.ValidateCouponAsync(CouponCode);
        if (coupon != null)
        {
            _cartService.ApplyCoupon(coupon);
            StatusMessage = $"Coupon '{coupon.Code}' applied: {coupon.Description}";
            CouponCode = string.Empty;
        }
        else
        {
            StatusMessage = $"Invalid coupon code: {CouponCode}";
        }
    }

    [RelayCommand]
    private void RemoveCoupon(string code)
    {
        _cartService.RemoveCoupon(code);
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
                        _cartService.SetCustomerDiscount(result.DiscountCard.Discount);
                        StatusMessage = $"Клиент: {result.FullName} • Скидка по карте: {result.DiscountCard.Discount}%";
                    }
                    else
                    {
                        _cartService.ClearCustomerDiscount();
                        StatusMessage = $"Выбран клиент: {result.FullName}";
                    }
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

    private async Task ResumeParkedSale(string id)
    {
        // Если в корзине уже есть товары — авто-отложить текущую продажу.
        if (_cartService.Items.Any())
        {
            await _parkedSaleService.ParkAsync(BuildSnapshot(null), TotalAmount);
            _cartService.ClearCart();
            _cartService.ClearCustomerDiscount();
            SelectedCustomer = null;
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
                        Products = _cartService.Items.Select(item => new DocumentProduct
                        {
                            Name = item.Product.Name,
                            ProductId = item.Product.Id,
                            Quantity = item.Quantity,
                            SellPrice = item.Product.Price,
                            PriceBeforeDiscount = item.Product.OriginalPrice ?? item.Product.Price,
                            DiscountPercent = item.Product.DiscountPercent ?? 0m
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
