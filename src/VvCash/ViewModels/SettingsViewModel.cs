using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Constants;
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using VvCash.Services.Data;
using VvCash.Services.Hardware;

namespace VvCash.ViewModels;

public partial class PrinterConfigViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _connectionString = string.Empty;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLan))]
    [NotifyPropertyChangedFor(nameof(IsUsbOrCom))]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel))]
    private PrinterConnectionType _connectionType;

    /// <summary>Каталог целиком: он неизменен и не зависит от сети — как
    /// AvailablePhoneFormats рядом.</summary>
    public IReadOnlyList<EscPosCodePage> AvailableCodePages { get; } = EscPosCodePages.All;

    /// <summary>Nullable по той же причине, что SelectedPhoneFormat: SelectingItemsControl
    /// приводит SelectedItem к null и пишет его обратно через TwoWay, если присвоенного
    /// значения не нашлось в ItemsSource.</summary>
    [ObservableProperty]
    private EscPosCodePage? _selectedCodePage = EscPosCodePages.Default;

    public ObservableCollection<string> AvailableConnections { get; } = new();

    public bool IsLan => ConnectionType == PrinterConnectionType.LAN;
    public bool IsUsbOrCom => ConnectionType == PrinterConnectionType.USB || ConnectionType == PrinterConnectionType.COM;

    public string ConnectionLabel => ConnectionType switch
    {
        PrinterConnectionType.LAN => "IP Address / Port",
        PrinterConnectionType.USB => "Select Printer",
        PrinterConnectionType.COM => "Select Port",
        _ => "Address"
    };

    partial void OnConnectionTypeChanged(PrinterConnectionType value)
    {
        UpdateAvailableConnections();
    }

    public void UpdateAvailableConnections()
    {
        AvailableConnections.Clear();
        if (ConnectionType == PrinterConnectionType.USB)
        {
            var printers = PrinterDiscoveryService.GetUsbPrinters();
            foreach (var printer in printers)
                AvailableConnections.Add(printer);
        }
        else if (ConnectionType == PrinterConnectionType.COM)
        {
            var ports = PrinterDiscoveryService.GetComPorts();
            foreach (var port in ports)
                AvailableConnections.Add(port);
        }

        if (IsUsbOrCom && !AvailableConnections.Contains(ConnectionString) && AvailableConnections.Any())
        {
            ConnectionString = AvailableConnections.First();
        }
        else if (IsLan && string.IsNullOrWhiteSpace(ConnectionString))
        {
            ConnectionString = "192.168.1.100:9100";
        }
    }
}

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly IOfflineStorageService _offlineStorageService;
    private readonly ICashFeatureService _features;
    private readonly IPaymentCategoryService? _paymentCategories;

    [ObservableProperty]
    private string _backendUrl = string.Empty;

    /// <summary>Why the last Save was refused, or empty when it went through. Shown on
    /// the settings screen itself: a Save that silently does nothing is worse than one
    /// that refuses out loud, because the register then runs on whatever was configured
    /// before while the screen shows what the operator thinks they just set.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    [ObservableProperty]
    private string _cashRegisterToken = string.Empty;

    [ObservableProperty]
    private string _syncIntervalText = string.Empty;

    public ObservableCollection<PrinterConfigViewModel> Printers { get; } = new();

    public ObservableCollection<string> AvailableLanguages { get; } = new() { "ru", "en", "tg", "uz", "kk" };

    [ObservableProperty]
    private string _selectedLanguage = "ru";

    /// <summary>Каталог целиком: он неизменен и не зависит от сети, поэтому
    /// подгружать его нечем и незачем.</summary>
    public IReadOnlyList<PhoneFormat> AvailablePhoneFormats { get; } = PhoneFormats.All;

    /// <summary>Nullable, как и SelectedPaymentCategory рядом: SelectingItemsControl
    /// приводит SelectedItem к null и пишет его обратно через TwoWay-привязку, если
    /// присвоенного значения не нашлось в ItemsSource. Сегодня оно находится всегда —
    /// но лишь потому, что Resolve возвращает экземпляры из All, а PhoneFormat не
    /// переопределяет Equals; на этот незаписанный инвариант опираться не стоит.</summary>
    [ObservableProperty]
    private PhoneFormat? _selectedPhoneFormat = PhoneFormats.Default;

    [ObservableProperty]
    private bool _returnOpenCashDrawer = true;

    [ObservableProperty]
    private bool _returnPrintReceipt = true;

    /// <summary>Payment categories offered for the exchange payout, loaded from
    /// GET /documents/payment/categories/. Empty when the register is offline or the
    /// role lacks documents.PaymentCategoryList — in which case the previously saved id
    /// is left alone rather than being cleared by an empty dropdown (see Save).</summary>
    public ObservableCollection<PaymentCategory> PaymentCategories { get; } = new();

    [ObservableProperty]
    private PaymentCategory? _selectedPaymentCategory;

    /// <summary>The same list, picked separately for the returns screen's payout —
    /// see ISettingsService.ReturnPayoutCategoryId for why the two are not one
    /// setting. Both dropdowns read <see cref="PaymentCategories"/>; a ComboBox does
    /// not mutate its ItemsSource, so one collection serves both.</summary>
    [ObservableProperty]
    private PaymentCategory? _selectedReturnPaymentCategory;

    /// <summary>What the register will actually do, which is the server's answer.
    /// The local fields behind the old checkboxes are kept and still saved, but no
    /// longer consulted — see ReturnsViewModel.
    ///
    /// Can read stale (show "enabled" even when the server has switched a flag off)
    /// on one specific path: this screen is reachable from the login screen, before
    /// any shift has been opened this run of the process, and ICashFeatureService's
    /// cache is only populated by PosViewModel.InitializeAsync — refreshing it any
    /// earlier is impossible, not just undone, because that same InitializeAsync is
    /// what creates the local database's Settings table in the first place
    /// (OfflineStorageService.InitializeAsync); reading the cache before that table
    /// exists would fail outright. Reordering startup to fix this display-only gap
    /// is deliberately out of scope: that sequencing already caused one production
    /// lockout (see the expired-session-escape fix), so it stays as-is for a toggle
    /// that is read-only here anyway.
    ///
    /// This never affects what actually happens on a return: RunPostReturnActionsAsync
    /// runs after a shift has opened, by which point InitializeAsync has already
    /// refreshed the real flags. Only what this screen *shows*, before that point,
    /// can lag behind — never what the register *does*.</summary>
    public bool ReturnOpenCashDrawerEffective => _features.Current.IsEnabled(CashFeatureCodes.ReturnOpenDrawer);
    public bool ReturnPrintReceiptEffective => _features.Current.IsEnabled(CashFeatureCodes.ReturnPrintReceipt);

    public Array ConnectionTypes => Enum.GetValues(typeof(PrinterConnectionType));

    public Action<ViewModelBase>? NavigationRequest { get; set; }
    private ViewModelBase _previousViewModel;

    public SettingsViewModel(ViewModelBase previousViewModel, ISettingsService settingsService,
        IOfflineStorageService offlineStorageService, ICashFeatureService features,
        IPaymentCategoryService? paymentCategories = null)
    {
        _previousViewModel = previousViewModel;
        _settingsService = settingsService;
        _offlineStorageService = offlineStorageService;
        _features = features;
        _paymentCategories = paymentCategories;

        // Load existing settings
        BackendUrl = _settingsService.BackendUrl;
        CashRegisterToken = _settingsService.CashRegisterToken;
        SyncIntervalText = _settingsService.SyncIntervalMinutes.ToString();
        SelectedLanguage = string.IsNullOrEmpty(_settingsService.Language) ? "ru" : _settingsService.Language;
        SelectedPhoneFormat = PhoneFormats.Resolve(_settingsService.PhoneFormatId);
        ReturnOpenCashDrawer = _settingsService.ReturnOpenCashDrawer;
        ReturnPrintReceipt = _settingsService.ReturnPrintReceipt;

        foreach (var printer in _settingsService.Printers)
        {
            var vm = new PrinterConfigViewModel
            {
                Name = printer.Name,
                ConnectionType = printer.ConnectionType,
                ConnectionString = printer.ConnectionString,
                IsEnabled = printer.IsEnabled,
                SelectedCodePage = EscPosCodePages.Resolve(printer.CodePageId)
            };
            vm.UpdateAvailableConnections();
            Printers.Add(vm);
        }

        _ = LoadPaymentCategoriesAsync();
    }

    private async Task LoadPaymentCategoriesAsync()
    {
        if (_paymentCategories == null) return;
        var categories = await _paymentCategories.GetPaymentCategoriesAsync();
        PaymentCategories.Clear();
        foreach (var c in categories) PaymentCategories.Add(c);
        SelectedPaymentCategory = PaymentCategories
            .FirstOrDefault(c => c.Id == _settingsService.ExchangePayoutCategoryId);
        SelectedReturnPaymentCategory = PaymentCategories
            .FirstOrDefault(c => c.Id == _settingsService.ReturnPayoutCategoryId);
    }

    [RelayCommand]
    private void AddPrinter()
    {
        var vm = new PrinterConfigViewModel
        {
            Name = "New Printer",
            ConnectionType = PrinterConnectionType.LAN,
            ConnectionString = "192.168.1.100:9100",
            IsEnabled = true,
            SelectedCodePage = EscPosCodePages.Default
        };
        vm.UpdateAvailableConnections();
        Printers.Add(vm);
    }

    [RelayCommand]
    private void RemovePrinter(PrinterConfigViewModel printer)
    {
        if (printer != null)
        {
            Printers.Remove(printer);
        }
    }

    /// <summary>Confirmation overlay state. This screen is reachable from the login screen,
    /// before anyone has authenticated, and on an offline register a wiped catalog means
    /// nothing can be sold until connectivity returns — so the two destructive buttons no
    /// longer do the work themselves. They arm <see cref="_pendingAction"/> and raise this.
    /// Shaped after PosViewModel's own IsShiftCloseConfirmVisible overlay rather than
    /// inventing a second confirmation pattern.</summary>
    [ObservableProperty] private bool _isConfirmVisible;

    [ObservableProperty] private string _confirmMessage = string.Empty;

    /// <summary>What <see cref="ConfirmCommand"/> will run. Cleared by both exits, so a
    /// stray second tap after a cancel cannot run an action the operator already refused.</summary>
    private Func<Task>? _pendingAction;

    private void AskToConfirm(string message, Func<Task> action)
    {
        _pendingAction = action;
        ConfirmMessage = message;
        IsConfirmVisible = true;
    }

    [RelayCommand]
    private async Task Confirm()
    {
        // Taken and cleared here, before the action runs — not what stops a double tap
        // (that is [RelayCommand]'s default AllowConcurrentExecutions = false on async
        // commands, which drops CanExecute while one is already running), but it is what
        // makes cancel-then-confirm a genuine no-op: nothing is left armed for a stray
        // Confirm to pick up once CancelConfirm has already run.
        var action = _pendingAction;
        _pendingAction = null;
        IsConfirmVisible = false;
        if (action == null) return;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            // The overlay is already down and the operator has no other signal — this
            // screen's own error banner is the one place a failed wipe can still be
            // reported. Silently swallowing it would leave them believing a catalog was
            // cleared that is still there, or half there.
            ErrorMessage = $"{I18nService.Instance["ClearFailed"]} {ex.Message}";
        }
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        _pendingAction = null;
        IsConfirmVisible = false;
    }

    [RelayCommand]
    private void ClearCategories()
        => AskToConfirm(I18nService.Instance["ConfirmClearCategories"], async () =>
        {
            await _offlineStorageService.ClearCategoriesAsync();
            await _offlineStorageService.SetLastSyncVersionAsync(0);
        });

    [RelayCommand]
    private void ClearProducts()
        => AskToConfirm(I18nService.Instance["ConfirmClearProducts"], async () =>
        {
            await _offlineStorageService.ClearProductsAsync();
            await _offlineStorageService.SetLastSyncVersionAsync(0);
        });

    [RelayCommand]
    private void GoBack()    {
        NavigationRequest?.Invoke(_previousViewModel);
    }

    /// <summary>Whether an address is somewhere this register may send its tokens.
    ///
    /// Every call carries the bearer token and the cash token, so plain http would put
    /// both on the wire in the clear. Loopback is the exception — the traffic never
    /// leaves the machine, and running the backend locally is how this gets debugged
    /// on site.
    ///
    /// A bare host ("example.test") is refused rather than silently prefixed: the
    /// register would build request URLs that go nowhere, and that surfaces hours later
    /// as "nothing syncs" rather than here, where it can still be fixed in one keystroke.</summary>
    private static bool IsAcceptableBackendUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }

    [RelayCommand]
    private void Save()
    {
        if (!IsAcceptableBackendUrl(BackendUrl))
        {
            ErrorMessage = "Адрес сервера должен начинаться с https:// (http:// допустим только для localhost).";
            return;
        }
        ErrorMessage = string.Empty;

        _settingsService.BackendUrl = BackendUrl;
        _settingsService.CashRegisterToken = CashRegisterToken;
        if (int.TryParse(SyncIntervalText, out int interval) && interval > 0)
        {
            _settingsService.SyncIntervalMinutes = interval;
        }
        else
        {
            _settingsService.SyncIntervalMinutes = 10;
        }

        _settingsService.Language = SelectedLanguage;
        I18nService.Instance.Initialize(SelectedLanguage);

        // Как и с категорией платежа ниже: пустой выбор — это не «формат сбросили»,
        // и записывать его поверх настроенной кассы нельзя.
        if (SelectedPhoneFormat != null)
            _settingsService.PhoneFormatId = SelectedPhoneFormat.Id;

        _settingsService.ReturnOpenCashDrawer = ReturnOpenCashDrawer;
        _settingsService.ReturnPrintReceipt = ReturnPrintReceipt;

        // Only when the list actually loaded: an offline settings visit shows an empty
        // dropdown and a null selection, and writing that through would silently
        // disable exchanges on a register that was configured correctly.
        if (SelectedPaymentCategory != null)
            _settingsService.ExchangePayoutCategoryId = SelectedPaymentCategory.Id;
        if (SelectedReturnPaymentCategory != null)
            _settingsService.ReturnPayoutCategoryId = SelectedReturnPaymentCategory.Id;

        _settingsService.Printers = Printers.Select(p => new PrinterConfig
        {
            Name = p.Name,
            ConnectionType = p.ConnectionType,
            ConnectionString = p.ConnectionString,
            IsEnabled = p.IsEnabled,
            // Здесь ?? Default, а не пропуск записи, как у SelectedPhoneFormat и
            // категорий платежа выше. Причина не в том, что каталог всегда полон —
            // PhoneFormats тоже статичен и сети не требует. Причин три другие:
            // Printers пересобирается списком целиком, поэтому «пропустить и
            // сохранить прежнее» потребовало бы сопоставлять каждую строку с её
            // прежним PrinterConfig, а у только что добавленной строки прежнего нет;
            // и цена промаха здесь несравнима — откат на CP866 это ровно то, что
            // Resolve и так отдаёт ненастроенному принтеру, тогда как пустой формат
            // телефона молча применил бы чужой код страны к настоящим номерам.
            CodePageId = (p.SelectedCodePage ?? EscPosCodePages.Default).Id
        }).ToList();

        _settingsService.Save();

        GoBack();
    }
}
