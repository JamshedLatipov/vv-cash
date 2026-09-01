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

    /// <summary>Идёт ли проверка по нажатию. Гасит кнопку на время запроса: кассир, не
    /// увидевший мгновенного ответа, жмёт ещё раз, а второй заход на сервер поверх
    /// первого ничего не добавляет.</summary>
    [ObservableProperty] private bool _isCheckingForUpdate;

    /// <summary>Ответ ручной проверки. Отдельно от ErrorText, который принадлежит
    /// установке: сбой проверки и сбой установки случаются в разных местах экрана и
    /// не должны затирать друг друга.</summary>
    [ObservableProperty] private string _checkResultText = string.Empty;

    /// <summary>The running build, formatted for the status bar.</summary>
    public string AppVersionText { get; }

    /// <summary>The version line for the dialog, formatted through the current locale.
    /// Built here rather than with a XAML StringFormat because the format string itself
    /// comes from I18nService at run time, which StringFormat cannot take.</summary>
    public string AvailableVersionText => AvailableUpdate is null
        ? string.Empty
        : string.Format(I18nService.Instance["UpdateVersionLine"], AvailableUpdate.Version);

    /// <summary>Set by App.axaml.cs to shut the desktop lifetime down. A settable
    /// delegate rather than a direct call, matching PosViewModel.NavigationRequest:
    /// the view model states intent, the host decides how it happens, and a test can
    /// observe it without an application.</summary>
    public Action? ShutdownRequested { get; set; }

    /// <summary>False while a receipt is in progress. Inno replaces the running exe, so
    /// installing mid-sale means losing whatever the cashier has rung up.</summary>
    public bool CanInstall => _cartService.Items.Count == 0 && !IsDownloading;

    /// <summary>True when a receipt is in progress. Distinct from !CanInstall, which is
    /// also false while a download is running — binding the "finish the current receipt"
    /// message to that would show it next to the progress bar, telling the cashier to
    /// finish a receipt they do not have.</summary>
    public bool IsBlockedByCart => _cartService.Items.Count > 0;

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
        _cartService.CartChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(IsBlockedByCart));
        };
    }

    partial void OnIsDownloadingChanged(bool value) => OnPropertyChanged(nameof(CanInstall));

    partial void OnAvailableUpdateChanged(UpdateInfo? value) => OnPropertyChanged(nameof(AvailableVersionText));

    /// <summary>Called from PosViewModel's background loop. Never throws — the service
    /// already swallows every failure and reports it as an outcome.
    ///
    /// Deliberately silent about failures: this runs while the cashier is serving a
    /// customer, and a network hiccup is not their problem. It retries in an hour.
    /// Only <see cref="CheckNowAsync"/> — the check a person asked for — reports why.</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        var result = await _updateService.CheckAsync(ct);
        if (result.Update is null) return;

        var info = result.Update;

        // The loop runs on a background thread (Task.Run, no captured UI context), and
        // these two properties are bound. Same idiom as PosViewModel's own
        // IsSystemOnline hand-off.
        Dispatcher.UIThread.Post(() =>
        {
            AvailableUpdate = info;
            IsUpdateAvailable = true;
        });
    }

    /// <summary>Проверка по нажатию кассира.
    ///
    /// Автопроверка ходит на сервер раз в час и только с открытого экрана продажи, а
    /// первый заход делает примерно через минуту после входа. Кассир, которому сказали,
    /// что релиз уже выложен, не может её поторопить ничем — и ждать час у него нет
    /// повода.
    ///
    /// Отвечает всегда, всеми тремя исходами. Молчание хоть на одном из них вернуло бы
    /// нерешаемую задачу: кнопка, которая иногда ничего не говорит, неотличима от
    /// сломанной, и кассир жмёт её снова.</summary>
    [RelayCommand]
    private async Task CheckNowAsync()
    {
        if (IsCheckingForUpdate) return;

        IsCheckingForUpdate = true;
        CheckResultText = string.Empty;
        ErrorText = null;

        try
        {
            var result = await _updateService.CheckAsync(CancellationToken.None);

            if (result.IsFailure)
            {
                CheckResultText = string.Format(
                    I18nService.Instance["UpdateCheckFailed"], result.Failure);
                return;
            }

            if (result.Update is null)
            {
                CheckResultText = string.Format(
                    I18nService.Instance["UpdateUpToDate"], AppVersionText);
                return;
            }

            AvailableUpdate = result.Update;
            IsUpdateAvailable = true;
            IsModalVisible = true;
        }
        finally
        {
            IsCheckingForUpdate = false;
        }
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
            // Captures SynchronizationContext.Current at construction, which is the
            // Avalonia UI context only because this method always runs from a command
            // binding started on the UI thread. A future caller invoking this from a
            // background thread would silently lose that marshalling.
            var progress = new Progress<double>(value => DownloadProgress = value);
            var path = await _updateService.DownloadAsync(AvailableUpdate, progress, _downloadCts.Token);

            if (path is null)
            {
                ErrorText = I18nService.Instance["UpdateDownloadFailed"];
                return;
            }

            // Re-check the cart here, right before Launch, rather than trust the
            // CanInstall read from before the await above: a 35 MB download takes long
            // enough for a barcode scan to land a product mid-flight. Once the
            // installer is running it will overwrite VvCash.exe regardless of what this
            // method does next, so the check has to gate Launch, not ShutdownRequested
            // — placed after Launch it would be worthless. The download is discarded by
            // simply not using it; UpdateService wipes its download directory at the
            // start of the next DownloadAsync call, so nothing needs cleaning up here.
            // A scan landing between this check and Launch below is still possible, but
            // that window is milliseconds rather than the seconds a download takes, and
            // closing it fully would mean blocking cart input during the download — a
            // larger change than the risk warrants.
            //
            // Nothing is written to ErrorText here on purpose. IsBlockedByCart is true by
            // definition at this point, and the dialog already shows UpdateBlockedByCart
            // permanently on that flag — setting the same sentence again would print it
            // twice, once above the other, at exactly the moment the cashier is trying to
            // read it.
            if (_cartService.Items.Count > 0) return;

            try
            {
                _launcher.Launch(path);
            }
            catch (Exception ex)
            {
                // The file is downloaded and verified — it just could not be started.
                // Show where it is so someone can double-click it. There is no logging
                // framework in this project; [UpdateViewModel] matches the prefix
                // UpdateService already uses for its own broad catches.
                Console.WriteLine($"[UpdateViewModel] Launch failed: {ex.GetType().Name}: {ex.Message}");
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
