using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Services;
using VvCash.Services.Update;

namespace VvCash.ViewModels;

public partial class UpdateViewModel : ViewModelBase
{
    private readonly IUpdateService _updateService;
    private readonly IInstallerLauncher _launcher;
    private readonly ICartService _cartService;
    private CancellationTokenSource? _downloadCts;

    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private bool _isModalVisible;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private UpdateInfo? _availableUpdate;

    /// <summary>The running build, formatted for the status bar.</summary>
    public string AppVersionText { get; }

    /// <summary>Set by App.axaml.cs to shut the desktop lifetime down. A settable
    /// delegate rather than a direct call, matching PosViewModel.NavigationRequest:
    /// the view model states intent, the host decides how it happens, and a test can
    /// observe it without an application.</summary>
    public Action? ShutdownRequested { get; set; }

    /// <summary>False while a receipt is in progress. Inno replaces the running exe, so
    /// installing mid-sale means losing whatever the cashier has rung up.</summary>
    public bool CanInstall => _cartService.Items.Count == 0 && !IsDownloading;

    public UpdateViewModel(
        IUpdateService updateService,
        IInstallerLauncher launcher,
        ICartService cartService,
        IAppVersionProvider versionProvider)
    {
        _updateService = updateService;
        _launcher = launcher;
        _cartService = cartService;

        AppVersionText = $"V {versionProvider.Current}";
        _cartService.CartChanged += (_, _) => OnPropertyChanged(nameof(CanInstall));
    }

    partial void OnIsDownloadingChanged(bool value) => OnPropertyChanged(nameof(CanInstall));

    /// <summary>Called from PosViewModel's background loop. Never throws — the service
    /// already swallows every failure and answers null.</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        var info = await _updateService.CheckAsync(ct);
        if (info is null) return;

        // The loop runs on a background thread (Task.Run, no captured UI context), and
        // these two properties are bound. Same idiom as PosViewModel's own
        // IsSystemOnline hand-off.
        Dispatcher.UIThread.Post(() =>
        {
            AvailableUpdate = info;
            IsUpdateAvailable = true;
        });
    }

    [RelayCommand]
    private void OpenModal()
    {
        ErrorText = null;
        IsModalVisible = true;
    }

    [RelayCommand]
    private void CloseModal() => IsModalVisible = false;

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    [RelayCommand]
    private async Task StartUpdateAsync()
    {
        if (AvailableUpdate is null || !CanInstall) return;

        ErrorText = null;
        DownloadProgress = 0;
        IsDownloading = true;
        _downloadCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<double>(value => DownloadProgress = value);
            var path = await _updateService.DownloadAsync(AvailableUpdate, progress, _downloadCts.Token);

            if (path is null)
            {
                ErrorText = I18nService.Instance["UpdateDownloadFailed"];
                return;
            }

            try
            {
                _launcher.Launch(path);
            }
            catch (Exception)
            {
                // The file is downloaded and verified — it just could not be started.
                // Show where it is so someone can double-click it.
                ErrorText = $"{I18nService.Instance["UpdateLaunchFailed"]} {path}";
                return;
            }

            // Inno cannot overwrite a running VvCash.exe, so the app has to get out of
            // the way. The installer's silent [Run] entry brings it back.
            ShutdownRequested?.Invoke();
        }
        finally
        {
            IsDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }
}
