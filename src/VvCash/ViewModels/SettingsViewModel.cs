using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Services;

namespace VvCash.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    private string _backendUrl = string.Empty;

    [ObservableProperty]
    private string _cashRegisterToken = string.Empty;

    [ObservableProperty]
    private string _syncIntervalText = string.Empty;

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

        _settingsService.Save();

        GoBack();
    }
}
