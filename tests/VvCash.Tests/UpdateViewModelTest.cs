using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Update;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

public class UpdateViewModelTest
{
    private sealed class FakeVersionProvider : IAppVersionProvider
    {
        public Version Current { get; set; } = new Version(1, 0, 0);
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        public UpdateInfo? Available;
        public string? DownloadResult;
        public int DownloadCalls;

        public Task<UpdateInfo?> CheckAsync(CancellationToken ct) => Task.FromResult(Available);

        public Task<string?> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
        {
            DownloadCalls++;
            progress?.Report(1.0);
            return Task.FromResult(DownloadResult);
        }
    }

    private sealed class FakeLauncher : IInstallerLauncher
    {
        public string? Launched;
        public Exception? Throw;

        public void Launch(string installerPath)
        {
            if (Throw is not null) throw Throw;
            Launched = installerPath;
        }
    }

    private static UpdateInfo SampleInfo() => new UpdateInfo(
        new Version(1, 1, 0),
        "https://proffi.io/downloads/proffi-kassa-setup.exe",
        "9f2b1c4a7e35d081bc6f42a90e5713d8cf20ab6749e83c15d02f7ba418c69e3d",
        35651584,
        "test build");

    private static (UpdateViewModel Vm, FakeUpdateService Service, FakeLauncher Launcher, CartService Cart)
        Build()
    {
        // StubPromotionProvider lives in tests/VvCash.Tests/StubPromotionProvider.cs and
        // is how every other test builds a real CartService without touching SQLite.
        var cart = new CartService(new StubPromotionProvider());
        var service = new FakeUpdateService();
        var launcher = new FakeLauncher();
        var vm = new UpdateViewModel(service, launcher, cart, new FakeVersionProvider());
        return (vm, service, launcher, cart);
    }

    private static Product SampleProduct() => new Product
    {
        Id = "p1",
        Name = "Product 1",
        Sku = "p1",
        Price = 10m
    };

    [Fact]
    public void VersionTextShowsTheRunningBuild()
    {
        var (vm, _, _, _) = Build();

        Assert.Equal("V 1.0.0", vm.AppVersionText);
    }

    [Fact]
    public async Task CheckRaisesTheBadgeWhenAReleaseIsAvailable()
    {
        var (vm, service, _, _) = Build();
        service.Available = SampleInfo();

        await vm.CheckAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsUpdateAvailable);
        Assert.Equal(new Version(1, 1, 0), vm.AvailableUpdate!.Version);
    }

    [Fact]
    public async Task AvailableVersionTextCarriesTheReleaseVersion()
    {
        var (vm, service, _, _) = Build();
        service.Available = SampleInfo();

        await vm.CheckAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        // I18nService.Instance is never Initialize()'d in this test suite (only
        // App.axaml.cs and SettingsViewModel call it), so "UpdateVersionLine" resolves
        // to the missing-key marker "[UpdateVersionLine]" rather than a real locale
        // string here — matching how SellerSwitchViewModelTest/PosViewModelSellerGateTest
        // compare against I18nService.Instance[key] instead of a hardcoded string, this
        // asserts against the same lookup the view model itself uses, so it holds
        // whether or not a locale ends up loaded.
        var expected = string.Format(I18nService.Instance["UpdateVersionLine"], vm.AvailableUpdate!.Version);
        Assert.Equal(expected, vm.AvailableVersionText);

        // Pin down that 1.1.0 is genuinely what flows into the format call, independent
        // of whether the locale template is present to interpolate it.
        Assert.Equal(new Version(1, 1, 0), vm.AvailableUpdate!.Version);
    }

    [Fact]
    public async Task CheckLeavesTheBadgeHiddenWhenThereIsNothingNew()
    {
        var (vm, service, _, _) = Build();
        service.Available = null;

        await vm.CheckAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsUpdateAvailable);
    }

    [Fact]
    public void InstallIsBlockedWhileTheCartHasItems()
    {
        var (vm, _, _, cart) = Build();

        Assert.True(vm.CanInstall);

        cart.AddProduct(SampleProduct());

        // Restarting the register mid-receipt would lose the sale in progress.
        Assert.False(vm.CanInstall);

        cart.ClearCart();

        Assert.True(vm.CanInstall);
    }

    [Fact]
    public async Task StartUpdateRefusesWhileTheCartHasItems()
    {
        var (vm, service, launcher, cart) = Build();
        service.Available = SampleInfo();
        service.DownloadResult = @"C:\temp\VvCashInstaller.exe";
        await vm.CheckAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        cart.AddProduct(SampleProduct());
        await vm.StartUpdateCommand.ExecuteAsync(null);

        Assert.Equal(0, service.DownloadCalls);
        Assert.Null(launcher.Launched);
    }

    [Fact]
    public async Task SuccessfulUpdateLaunchesTheInstallerAndAsksForShutdown()
    {
        var (vm, service, launcher, _) = Build();
        service.Available = SampleInfo();
        service.DownloadResult = @"C:\temp\VvCashInstaller.exe";
        var shutdownRequested = false;
        vm.ShutdownRequested = () => shutdownRequested = true;

        await vm.CheckAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        await vm.StartUpdateCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\temp\VvCashInstaller.exe", launcher.Launched);
        Assert.True(shutdownRequested);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public async Task FailedDownloadShowsAnErrorAndDoesNotShutDown()
    {
        var (vm, service, launcher, _) = Build();
        service.Available = SampleInfo();
        service.DownloadResult = null;
        var shutdownRequested = false;
        vm.ShutdownRequested = () => shutdownRequested = true;

        await vm.CheckAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        await vm.StartUpdateCommand.ExecuteAsync(null);

        Assert.Null(launcher.Launched);
        Assert.False(shutdownRequested);
        Assert.False(string.IsNullOrEmpty(vm.ErrorText));
        Assert.False(vm.IsDownloading);
    }

    [Fact]
    public async Task FailedLaunchShowsThePathSoItCanBeRunByHand()
    {
        var (vm, service, launcher, _) = Build();
        service.Available = SampleInfo();
        service.DownloadResult = @"C:\temp\VvCashInstaller.exe";
        launcher.Throw = new System.ComponentModel.Win32Exception("access denied");
        var shutdownRequested = false;
        vm.ShutdownRequested = () => shutdownRequested = true;

        await vm.CheckAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        await vm.StartUpdateCommand.ExecuteAsync(null);

        Assert.False(shutdownRequested);
        Assert.Contains(@"C:\temp\VvCashInstaller.exe", vm.ErrorText);
    }

    [Fact]
    public async Task DismissHidesTheModalButKeepsTheBadge()
    {
        var (vm, service, _, _) = Build();
        service.Available = SampleInfo();
        await vm.CheckAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        vm.OpenModalCommand.Execute(null);
        Assert.True(vm.IsModalVisible);

        vm.CloseModalCommand.Execute(null);

        // "Later" is not "never" — the badge stays so the cashier can come back after
        // closing the shift.
        Assert.False(vm.IsModalVisible);
        Assert.True(vm.IsUpdateAvailable);
    }
}
