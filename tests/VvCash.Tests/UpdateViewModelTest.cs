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
        public string? CheckFailure;
        public string? DownloadResult;
        public int DownloadCalls;
        public int CheckCalls;

        /// <summary>Invoked right before DownloadAsync returns, standing in for
        /// something happening on the register while the (real, seconds-long) download
        /// is in flight — used to reproduce a scan landing mid-download.</summary>
        public Action? OnDownload;

        /// <summary>Держит проверку незавершённой, пока тест не отпустит. Без него
        /// фейк отвечает синхронно, первый вызов успевает закончиться до второго, и
        /// проверить защиту от повторного нажатия нечем — она просто не задействуется.</summary>
        public TaskCompletionSource<bool>? Gate;

        public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
        {
            CheckCalls++;
            if (Gate is not null) await Gate.Task;

            return CheckFailure is not null
                ? UpdateCheckResult.Failed(CheckFailure)
                : Available is not null
                    ? UpdateCheckResult.Found(Available)
                    : UpdateCheckResult.UpToDate();
        }

        public Task<string?> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
        {
            DownloadCalls++;
            progress?.Report(1.0);
            OnDownload?.Invoke();
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
    public async Task StartUpdateRefusesWhenAScanLandsDuringTheDownload()
    {
        var (vm, service, launcher, cart) = Build();
        service.Available = SampleInfo();
        service.DownloadResult = @"C:\temp\VvCashInstaller.exe";
        // The cart was empty when StartUpdateCommand read CanInstall, but a barcode
        // scan lands a product while the (roughly 35 MB) download is in flight.
        service.OnDownload = () => cart.AddProduct(SampleProduct());
        var shutdownRequested = false;
        vm.ShutdownRequested = () => shutdownRequested = true;

        await vm.CheckAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        await vm.StartUpdateCommand.ExecuteAsync(null);

        // The download did happen (it started before the scan), but the installer must
        // never run against a cart that gained items while it was in flight.
        Assert.Equal(1, service.DownloadCalls);
        Assert.Null(launcher.Launched);
        Assert.False(shutdownRequested);

        // The dialog explains itself through IsBlockedByCart, which drives the permanent
        // "finish the current receipt" line. ErrorText deliberately stays clear so that
        // sentence is not printed twice.
        Assert.True(vm.IsBlockedByCart);
        Assert.True(string.IsNullOrEmpty(vm.ErrorText));
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

    [Fact]
    public void IsBlockedByCartTracksTheCart()
    {
        var (vm, _, _, cart) = Build();

        Assert.False(vm.IsBlockedByCart);

        cart.AddProduct(SampleProduct());
        Assert.True(vm.IsBlockedByCart);

        cart.ClearCart();
        Assert.False(vm.IsBlockedByCart);
    }

    // ---------------------------------------------------------------------------------
    // Ручная проверка обновления
    // ---------------------------------------------------------------------------------

    // Автопроверка ходит на сервер раз в час и только с открытого экрана продажи.
    // Кассир, который знает, что релиз уже выложен, не может её поторопить ничем — а
    // ждать час у него нет ни времени, ни повода. Отсюда кнопка.
    //
    // Три исхода, и все три обязаны быть различимы. Молчание на любом из них вернуло бы
    // ровно ту болезнь, из-за которой этот день и случился: касса, которая не отвечает,
    // неотличима от кассы, у которой всё хорошо.

    [Fact]
    public async Task CheckNow_WhenAnUpdateIsThere_ShowsItAndOpensTheDialog()
    {
        var (vm, service, _, _) = Build();
        service.Available = SampleInfo();

        await vm.CheckNowCommand.ExecuteAsync(null);

        Assert.True(vm.IsUpdateAvailable);
        Assert.True(vm.IsModalVisible);
        Assert.False(vm.IsCheckingForUpdate);
    }

    [Fact]
    public async Task CheckNow_WhenAlreadyCurrent_SaysSoInsteadOfSayingNothing()
    {
        // Самый частый исход и самый опасный: без ответа кассир решит, что кнопка
        // сломана, и пойдёт жать её ещё раз.
        var (vm, _, _, _) = Build();

        await vm.CheckNowCommand.ExecuteAsync(null);

        Assert.False(vm.IsUpdateAvailable);
        Assert.False(vm.IsModalVisible);
        Assert.NotEmpty(vm.CheckResultText);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public async Task CheckNow_WhenTheCheckItselfFailed_SaysWhy()
    {
        // «Обновлений нет» и «до сервера не достучались» — разные новости, и чинятся
        // они в разных местах. Раньше CheckAsync возвращал null на оба случая, потому
        // что единственным вызывающим был часовой таймер, которому разница не нужна.
        // У кнопки она нужна.
        var (vm, service, _, _) = Build();
        service.CheckFailure = "The SSL connection could not be established";

        await vm.CheckNowCommand.ExecuteAsync(null);

        // Сравнение с самим I18nService, а не с готовым текстом: в тестовом хосте
        // Avalonia не поднята, словарь пуст, и любой ключ отдаёт заглушку "[ключ]" —
        // подставленная причина в неё не попадает, потому что в заглушке нет {0}.
        // Проверить здесь можно только «какая ветка выбрана», и это делается через
        // разные ключи. Что {0} в переводе на месте и причина реально доезжает до
        // кассира, стережёт I18nLocaleTest.
        Assert.Equal(
            string.Format(I18nService.Instance["UpdateCheckFailed"], service.CheckFailure),
            vm.CheckResultText);
        Assert.NotEqual(
            string.Format(I18nService.Instance["UpdateUpToDate"], vm.AppVersionText),
            vm.CheckResultText);

        Assert.False(vm.IsUpdateAvailable);
        Assert.False(vm.IsCheckingForUpdate);
    }

    [Fact]
    public async Task CheckNow_DoesNotRunTwiceAtOnce()
    {
        // Кассир, не увидевший мгновенного ответа, жмёт ещё раз. Второй заход на
        // сервер поверх первого ничего не добавляет.
        //
        // Проверка держится незавершённой через Gate: без него фейк отвечает
        // синхронно, первый вызов заканчивается раньше, чем начинается второй, и тест
        // проходил бы, ничего при этом не проверяя.
        var (vm, service, _, _) = Build();
        var gate = new TaskCompletionSource<bool>();
        service.Gate = gate;

        var first = vm.CheckNowCommand.ExecuteAsync(null);
        var second = vm.CheckNowCommand.ExecuteAsync(null);
        gate.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.Equal(1, service.CheckCalls);
    }

    [Theory]
    [InlineData("CheckNowCommand")]
    [InlineData("IsCheckingForUpdate")]
    [InlineData("CheckResultText")]
    [InlineData("AppVersionText")]
    public void PosViewBindingPaths_ResolveOnTheViewModel(string path)
    {
        // AvaloniaUseCompiledBindingsByDefault выключен: опечатка в пути собирается
        // начисто и молча даёт мёртвую кнопку. Свойство команды генерирует
        // CommunityToolkit из [RelayCommand], грепом его в исходнике не найти —
        // поэтому отражением.
        var property = typeof(UpdateViewModel).GetProperty(path);

        Assert.NotNull(property);
        Assert.True(property!.GetMethod?.IsPublic, $"{path} не читается привязкой");
    }

    [Fact]
    public async Task HourlyCheck_StaysSilentWhenTheCheckFails()
    {
        // Фоновая проверка не должна беспокоить кассира сбоями сети: она повторится
        // через час, а кассир в этот момент обслуживает покупателя. Сообщать о причине
        // обязана только та проверка, которую человек запросил сам.
        var (vm, service, _, _) = Build();
        service.CheckFailure = "no network";

        await vm.CheckAsync(CancellationToken.None);

        Assert.Empty(vm.CheckResultText);
        Assert.False(vm.IsUpdateAvailable);
    }
}
