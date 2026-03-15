using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VvCash.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _backendUrl = string.Empty;

    [ObservableProperty]
    private string _cashRegisterToken = string.Empty;

    public Action<ViewModelBase>? NavigationRequest { get; set; }
    private ViewModelBase _previousViewModel;

    public SettingsViewModel(ViewModelBase previousViewModel)
    {
        _previousViewModel = previousViewModel;
    }

    [RelayCommand]
    private void GoBack()
    {
        NavigationRequest?.Invoke(_previousViewModel);
    }

    [RelayCommand]
    private void Save()
    {
        // For now, just go back. Later we can save these to a config file.
        GoBack();
    }
}
