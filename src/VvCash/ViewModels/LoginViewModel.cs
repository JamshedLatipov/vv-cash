using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Services.Api;

namespace VvCash.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    private readonly IAuthService _authService;

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy = false;

    [ObservableProperty]
    private bool _rememberMe = false;

    public event EventHandler? LoginSuccessful;
    public event EventHandler? SettingsRequested;

    public LoginViewModel(IAuthService authService)
    {
        _authService = authService;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The login screen's way out of the app. It exists because the window is now
    /// borderless and full screen (see MainWindow.axaml) — there is no system X to click, and
    /// the POS's own power button lives behind the login. No exit menu here on purpose: with no
    /// session and no shift, "exit" has only one meaning, so this closes outright.
    ///
    /// MainWindow.OnClosing only intercepts while a PosViewModel is on screen, so this needs no
    /// equivalent of PosViewModel.IsExitConfirmed to get past it.</summary>
    [RelayCommand]
    private void ExitApplication()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow?.Close();
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        ErrorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Please enter both email and password";
            return;
        }

        IsBusy = true;

        try
        {
            bool success = await _authService.LoginAsync(Email, Password, RememberMe);

            if (success)
            {
                LoginSuccessful?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                ErrorMessage = "Invalid credentials or unable to connect.";
            }
        }
        catch (Exception)
        {
            ErrorMessage = "An error occurred during login.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
