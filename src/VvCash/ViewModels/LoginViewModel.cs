using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VvCash.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaskedPin))]
    private string _pin = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public string MaskedPin => new string('•', Pin.Length);

    public event EventHandler? LoginSuccessful;
    public event EventHandler? SettingsRequested;

    [RelayCommand]
    private void OpenSettings()
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }


    [RelayCommand]
    private void AddDigit(string digit)
    {
        ErrorMessage = string.Empty;
        if (Pin.Length < 6) // Standard 4-6 digit PIN
        {
            Pin += digit;
        }
    }

    [RelayCommand]
    private void RemoveDigit()
    {
        ErrorMessage = string.Empty;
        if (Pin.Length > 0)
        {
            Pin = Pin[..^1];
        }
    }

    [RelayCommand]
    private void Clear()
    {
        ErrorMessage = string.Empty;
        Pin = string.Empty;
    }

    [RelayCommand]
    private void Login()
    {
        if (string.IsNullOrEmpty(Pin))
        {
            ErrorMessage = "Please enter a PIN";
            return;
        }

        // Simulating login logic. Accept any PIN for this prototype.
        LoginSuccessful?.Invoke(this, EventArgs.Empty);
    }
}
