using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Constants;
using VvCash.Models;
using VvCash.Services;
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

    [ObservableProperty]
    private string _backendUrl = string.Empty;

    [ObservableProperty]
    private string _cashRegisterToken = string.Empty;

    [ObservableProperty]
    private string _syncIntervalText = string.Empty;

    public ObservableCollection<PrinterConfigViewModel> Printers { get; } = new();

    public ObservableCollection<string> AvailableLanguages { get; } = new() { "ru", "en", "tg", "uz", "kk" };

    [ObservableProperty]
    private string _selectedLanguage = "ru";

    [ObservableProperty]
    private bool _returnOpenCashDrawer = true;

    [ObservableProperty]
    private bool _returnPrintReceipt = true;

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
        IOfflineStorageService offlineStorageService, ICashFeatureService features)
    {
        _previousViewModel = previousViewModel;
        _settingsService = settingsService;
        _offlineStorageService = offlineStorageService;
        _features = features;

        // Load existing settings
        BackendUrl = _settingsService.BackendUrl;
        CashRegisterToken = _settingsService.CashRegisterToken;
        SyncIntervalText = _settingsService.SyncIntervalMinutes.ToString();
        SelectedLanguage = string.IsNullOrEmpty(_settingsService.Language) ? "ru" : _settingsService.Language;
        ReturnOpenCashDrawer = _settingsService.ReturnOpenCashDrawer;
        ReturnPrintReceipt = _settingsService.ReturnPrintReceipt;

        foreach (var printer in _settingsService.Printers)
        {
            var vm = new PrinterConfigViewModel
            {
                Name = printer.Name,
                ConnectionType = printer.ConnectionType,
                ConnectionString = printer.ConnectionString,
                IsEnabled = printer.IsEnabled
            };
            vm.UpdateAvailableConnections();
            Printers.Add(vm);
        }
    }

    [RelayCommand]
    private void AddPrinter()
    {
        var vm = new PrinterConfigViewModel
        {
            Name = "New Printer",
            ConnectionType = PrinterConnectionType.LAN,
            ConnectionString = "192.168.1.100:9100",
            IsEnabled = true
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

    [RelayCommand]
    private async Task ClearCategories()
    {
        await _offlineStorageService.ClearCategoriesAsync();
        await _offlineStorageService.SetLastSyncVersionAsync(0);
    }

    [RelayCommand]
    private async Task ClearProducts()
    {
        await _offlineStorageService.ClearProductsAsync();
        await _offlineStorageService.SetLastSyncVersionAsync(0);
    }

    [RelayCommand]
    private async Task ClearUnsyncedDocuments()
    {
        await _offlineStorageService.ClearUnsyncedDocumentsAsync();
    }

    [RelayCommand]
    private void GoBack()    {
        NavigationRequest?.Invoke(_previousViewModel);
    }

    [RelayCommand]
    private void Save()
    {
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

        _settingsService.ReturnOpenCashDrawer = ReturnOpenCashDrawer;
        _settingsService.ReturnPrintReceipt = ReturnPrintReceipt;

        _settingsService.Printers = Printers.Select(p => new PrinterConfig
        {
            Name = p.Name,
            ConnectionType = p.ConnectionType,
            ConnectionString = p.ConnectionString,
            IsEnabled = p.IsEnabled
        }).ToList();

        _settingsService.Save();

        GoBack();
    }
}
