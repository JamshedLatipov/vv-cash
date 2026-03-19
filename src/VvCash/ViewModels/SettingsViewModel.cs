using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Models;
using VvCash.Services;
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

    public Array ConnectionTypes => Enum.GetValues(typeof(PrinterConnectionType));

    public Action<ViewModelBase>? NavigationRequest { get; set; }
    private ViewModelBase _previousViewModel;

    public SettingsViewModel(ViewModelBase previousViewModel, ISettingsService settingsService)
    {
        _previousViewModel = previousViewModel;
        _settingsService = settingsService;

        // Load existing settings
        BackendUrl = _settingsService.BackendUrl;
        CashRegisterToken = _settingsService.CashRegisterToken;
        SyncIntervalText = _settingsService.SyncIntervalMinutes.ToString();
        SelectedLanguage = string.IsNullOrEmpty(_settingsService.Language) ? "ru" : _settingsService.Language;

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
    private void GoBack()
    {
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
