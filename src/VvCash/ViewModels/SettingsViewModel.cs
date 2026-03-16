using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Models;
using VvCash.Services;

namespace VvCash.ViewModels;

public partial class PrinterConfigViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private PrinterConnectionType _connectionType;

    [ObservableProperty]
    private string _connectionString = string.Empty;

    [ObservableProperty]
    private bool _isEnabled = true;
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

        foreach (var printer in _settingsService.Printers)
        {
            Printers.Add(new PrinterConfigViewModel
            {
                Name = printer.Name,
                ConnectionType = printer.ConnectionType,
                ConnectionString = printer.ConnectionString,
                IsEnabled = printer.IsEnabled
            });
        }
    }

    [RelayCommand]
    private void AddPrinter()
    {
        Printers.Add(new PrinterConfigViewModel
        {
            Name = "New Printer",
            ConnectionType = PrinterConnectionType.LAN,
            ConnectionString = "192.168.1.100:9100",
            IsEnabled = true
        });
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
