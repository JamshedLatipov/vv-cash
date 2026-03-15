using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Hardware;

namespace VvCash.ViewModels;

public partial class PosViewModel : ViewModelBase
{
    private readonly IProductService _productService;
    private readonly ICartService _cartService;
    private readonly IDiscountService _discountService;
    private readonly IPrinterService _printerService;
    private readonly ICustomerDisplayService _customerDisplayService;

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private ObservableCollection<Product> _products = new();
    [ObservableProperty] private ObservableCollection<CartItem> _cartItems = new();
    [ObservableProperty] private ObservableCollection<string> _categories = new();
    [ObservableProperty] private string _selectedCategory = "All";
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

    public CustomerDisplayViewModel? CustomerDisplayViewModel { get; set; }

    public PosViewModel(
        IProductService productService,
        ICartService cartService,
        IDiscountService discountService,
        IPrinterService printerService,
        ICustomerDisplayService customerDisplayService)
    {
        _productService = productService;
        _cartService = cartService;
        _discountService = discountService;
        _printerService = printerService;
        _customerDisplayService = customerDisplayService;

        _cartService.CartChanged += OnCartChanged;
        _printerService.StatusChanged += OnPrinterStatusChanged;

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        var cats = await _productService.GetCategoriesAsync();
        Categories = new ObservableCollection<string>(cats);
        await LoadProductsAsync("All");
    }

    private async Task LoadProductsAsync(string category)
    {
        var products = string.IsNullOrWhiteSpace(SearchQuery)
            ? await _productService.GetProductsByCategoryAsync(category)
            : await _productService.SearchProductsAsync(SearchQuery);
        Products = new ObservableCollection<Product>(products);
    }

    partial void OnSearchQueryChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !IsCatalogOpen)
        {
            IsCatalogOpen = true;
        }
        _ = LoadProductsAsync(SelectedCategory);
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
        await LoadProductsAsync(SelectedCategory);
    }

    [RelayCommand]
    private async Task SelectCategory(string category)
    {
        SelectedCategory = category;
        SearchQuery = string.Empty;
        IsCatalogOpen = true;
        await LoadProductsAsync(category);
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
        // Simple integration point assuming IClassicDesktopStyleApplicationLifetime or parent window lookup
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
    private async Task Pay()
    {
        if (!CartItems.Any()) return;

        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                var dialog = new VvCash.Views.MixedPaymentWindow();
                dialog.DataContext = new MixedPaymentViewModel(dialog, TotalAmount);
                var result = await dialog.ShowDialog<bool>(mainWindow);

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
            }
        }
    }

    public async Task HandleBarcodeAsync(string barcode)
    {
        var product = await _productService.GetProductByBarcodeAsync(barcode);
        if (product != null)
        {
            AddToCart(product);
        }
        else
        {
            StatusMessage = $"Product not found for barcode: {barcode}";
        }
    }
}
