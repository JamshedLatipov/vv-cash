using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VvCash.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _backendUrl = string.Empty;

    [ObservableProperty]
    private string _cashToken = string.Empty;

    public event EventHandler? CloseRequested;

    public SettingsViewModel()
    {
    }

    [RelayCommand]
    private void Save()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
