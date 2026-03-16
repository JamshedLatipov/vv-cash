using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Api;
using VvCash.Services.Data;
using VvCash.Services.Hardware;

namespace VvCash.ViewModels;

public partial class PosViewModel : ViewModelBase
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
    private CancellationTokenSource? _syncCancellationTokenSource;

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private ObservableCollection<Product> _products = new();
    [ObservableProperty] private ObservableCollection<CartItem> _cartItems = new();
    [ObservableProperty] private ObservableCollection<Category> _allCategories = new();
    [ObservableProperty] private ObservableCollection<Category> _quickCategories = new();
    [ObservableProperty] private Category? _selectedCategory;
    public string SelectedCategoryName => SelectedCategory?.Name ?? "All Categories";
    [ObservableProperty] private bool _isViewingCategories = true;
    [ObservableProperty] private string _couponCode = string.Empty;
    [ObservableProperty] private ObservableCollection<Coupon> _appliedCoupons = new();
    [ObservableProperty] private decimal _subtotal;
    [ObservableProperty] private decimal _tax;
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
    [ObservableProperty] private bool _isAlertModalVisible = false;
    [ObservableProperty] private string _alertMessage = string.Empty;

    public CustomerDisplayViewModel? CustomerDisplayViewModel { get; set; }
    public Action<ViewModelBase>? NavigationRequest { get; set; }

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
    private async Task CloseShiftAsync()
    {
        Console.WriteLine("[PosViewModel] CloseShiftAsync command executed.");
        System.Diagnostics.Debug.WriteLine("[PosViewModel] CloseShiftAsync command executed.");
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
        await LoadProductsAsync(SelectedCategory?.Id);
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
        ISettingsService settingsService)
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
            while (!token.IsCancellationRequested)
            {
                await _syncService.SyncProductsAsync();
                int intervalMinutes = _settingsService.SyncIntervalMinutes;
                if (intervalMinutes <= 0) intervalMinutes = 10;

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), token);
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

        StartBackgroundSync();

        var allCats = await _categoryService.GetCategoriesAsync();
        var quickCats = await _categoryService.GetQuickAccessCategoriesAsync();
        AllCategories = new ObservableCollection<Category>(allCats);
        QuickCategories = new ObservableCollection<Category>(quickCats);
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
        Products = new ObservableCollection<Product>(products);
    }

    partial void OnSearchQueryChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !IsCatalogOpen)
        {
            IsCatalogOpen = true;
        }
        _ = LoadProductsAsync(SelectedCategory?.Id);
    }

    private void OnCartChanged(object? sender, EventArgs e)
    {
        CartItems = new ObservableCollection<CartItem>(_cartService.Items);
        AppliedCoupons = new ObservableCollection<Coupon>(_cartService.AppliedCoupons);
        Subtotal = _cartService.Subtotal;
        Tax = _cartService.Tax;
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
        IsPrinterReady = status == PrinterStatus.Ready;
        PrinterStatusText = status switch
        {
            PrinterStatus.Ready => "Printer Ready",
            PrinterStatus.NoPaper => "No Paper",
            PrinterStatus.Error => "Printer Error",
            PrinterStatus.Offline => "Printer Offline",
            _ => "Unknown"
        };
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
        SelectedCategory = category;
        OnPropertyChanged(nameof(SelectedCategoryName));
        SearchQuery = string.Empty;
        IsCatalogOpen = true;

        if (category == null)
        {
            IsViewingCategories = true;
            Products.Clear();
        }
        else
        {
            IsViewingCategories = false;
            await LoadProductsAsync(category.Id);
        }
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
    private async Task PrintReceipt()
    {
        if (!CartItems.Any()) return;
        var success = await _printerService.PrintReceiptAsync(
            _cartService.Items,
            Subtotal, Tax, TotalDiscount, TotalAmount,
            _cartService.AppliedCoupons);
        StatusMessage = success ? "Receipt printed." : "Print failed.";
    }

    [RelayCommand]
    private async Task OpenCustomerRegistration()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                var dialog = new VvCash.Views.CustomerRegistrationWindow();
                dialog.DataContext = new CustomerRegistrationViewModel(dialog);
                await dialog.ShowDialog(mainWindow);
            }
        }
    }

    [RelayCommand]
    private void Pay()
    {
        if (!CartItems.Any()) return;

        if (NavigationRequest != null)
        {
            var mixedPaymentVm = new MixedPaymentViewModel(TotalAmount, async (result) =>
            {
                if (result)
                {
                    await _printerService.PrintReceiptAsync(
                        _cartService.Items,
                        Subtotal, Tax, TotalDiscount, TotalAmount,
                        _cartService.AppliedCoupons);
                    _cartService.ClearCart();
                    StatusMessage = "Payment processed. Thank you!";
                    if (CustomerDisplayViewModel != null)
                    {
                        CustomerDisplayViewModel.IsIdle = true;
                        CustomerDisplayViewModel.WelcomeMessage = "Thank you! Come again!";
                    }
                    _ = _customerDisplayService.ShowLineAsync("Thank you!", "Come again!");
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
