using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Hardware;
using VvCash.Services.Hardware.Protocols;
using Xunit;

namespace VvCash.Tests;

public class CustomerDisplayTest
{
    [Fact]
    public async Task NullDisplay_ReportsSuccess()
    {
        // Касса без VFD — нормальное состояние, а не отказ.
        var display = new NullCustomerDisplayService();

        Assert.True(await display.ShowTotalAsync(100m));
        Assert.True(await display.ClearAsync());
    }

    [Fact]
    public async Task Vfd_WhenThePortCannotBeOpened_ReportsFailure()
    {
        // До правки SendAsync ловил всё и писал в Console, то есть отказ порта был
        // неотличим от успеха — ровно та болезнь, которую чинит проблема 1.
        //
        // Имя заведомо некорректное, а не просто отсутствующее: Open() бросит
        // ArgumentException, тогда как выдернутый COM3 бросил бы FileNotFoundException.
        // Ловятся одинаково, но имя вида COM250 на чужой машине может внезапно
        // разрешиться в настоящее железо, а это — никогда.
        var display = new VfdDisplayService("COM-does-not-exist", 9600, VvCash.Models.EscPosCodePages.Cp866);

        Assert.False(await display.ShowTotalAsync(100m));
    }

    [Fact]
    public void Vfd_DoesNotPrintACurrencySymbol()
    {
        // Магазины не берут доллары; на чеке это уже чинили. Проверять надо
        // Build*Frame, а не BuildFrame: символ жил в форматировании суммы, и
        // набивка колонок его подставить не может в принципе. Кадр переехал в
        // EscPosDisplayProtocol, проверка осталась той же.
        Assert.DoesNotContain("$", EscPosDisplayProtocol.BuildTotalFrame(100m));
        Assert.Contains("100.00", EscPosDisplayProtocol.BuildTotalFrame(100m));

        Assert.DoesNotContain("$", EscPosDisplayProtocol.BuildItemFrame("Молоко", 50m));
        Assert.Contains("50.00", EscPosDisplayProtocol.BuildItemFrame("Молоко", 50m));
    }

    [Fact]
    public void Vfd_RendersTwentyColumnsPerLine()
    {
        // 40 и число пробелов ниже привязаны к Columns = 20 в DisplayText.
        // Columns — internal const и тесту не виден: если значение изменится, здесь
        // просто перестанет совпадать длина, без подсказки почему.
        var frame = EscPosDisplayProtocol.BuildFrame("Молоко", "50.00");

        Assert.Equal(40, frame.Length);
        Assert.StartsWith("Молоко" + new string(' ', 14), frame);
    }

    [Fact]
    public void Vfd_DefaultsToTheShippedProtocolAndFraming()
    {
        // Три необязательных параметра конструктора существуют ради вызовов, которым
        // нечего про них сказать. Их умолчания обязаны совпадать с тем, как касса
        // работала до появления протоколов, иначе «необязательный» означало бы
        // «молча меняющий поведение».
        var display = new VfdDisplayService("COM-does-not-exist", 9600, EscPosCodePages.Cp866);

        Assert.Same(DisplayProtocols.EscPos, display.Protocol);
        Assert.Same(SerialFramings.EightN1, display.Framing);
        Assert.False(display.DtrRts);
    }

    [Fact]
    public async Task Vfd_ProbeOnADeadPort_ReportsFailureLikeAnyOtherSend()
    {
        // Пробник идёт через ту же очередь и тот же catch, что и остальные кадры —
        // отдельного пути в порт у него нет.
        var display = new VfdDisplayService("COM-does-not-exist", 9600, EscPosCodePages.Cp866);

        Assert.False(await display.ShowProbeAsync(7));
    }

    [Fact]
    public async Task Vfd_TwoOverlappingSends_BothCompleteAndFailIndependently()
    {
        // Task.Run однажды снял неявную сериализацию, которую неожидаемым вызовам давал
        // UI-поток: очистка корзины — это четыре отправки одним кликом (ClearCart шлёт
        // ClearAsync поверх кадров, которые подняли её собственные CartChanged). На живом
        // порту проигравший поток получал бы UnauthorizedAccessException на Open(), catch
        // его глотал бы, и кадр пропадал бы молча — какой именно уцелеет, было бы не
        // определено. Настоящего порта здесь нет, но очередь всё равно обязана не терять
        // и не подвешивать ни один вызов: оба await должны завершиться, и оба — честно
        // вернуть false, а не один зависнуть навсегда или бросить мимо catch.
        //
        // AddToCart в этот список больше не входит: он шлёт ровно один кадр (см.
        // PosViewModel.PushToCustomerDisplay). Наложение отправок от этого не стало
        // невозможным — очистка корзины его всё ещё даёт, — поэтому тест остаётся.
        var display = new VfdDisplayService("COM-does-not-exist", 9600, EscPosCodePages.Cp866);

        var first = display.ShowTotalAsync(10m);
        var second = display.ShowItemAsync("Молоко", 5m);

        Assert.False(await first);
        Assert.False(await second);
    }

    private sealed class FakeSettings : ISettingsService
    {
        public string BackendUrl { get; set; } = "https://example.test/api/v1/";
        public string CashRegisterToken { get; set; } = "";
        public string AuthToken { get; set; } = "";
        public DateTime? AuthTokenExpiresAt { get; set; }
        public int SyncIntervalMinutes { get; set; } = 10;
        public string Language { get; set; } = "ru";
        public List<PrinterConfig> Printers { get; set; } = new();
        public bool ReturnOpenCashDrawer { get; set; } = true;
        public bool ReturnPrintReceipt { get; set; } = true;
        public string ExchangePayoutCategoryId { get; set; } = string.Empty;
        public string ReturnPayoutCategoryId { get; set; } = string.Empty;
        public string PhoneFormatId { get; set; } = string.Empty;
        public string CustomerDisplayPort { get; set; } = string.Empty;
        public int CustomerDisplayBaudRate { get; set; } = 9600;
        public string CustomerDisplayCodePageId { get; set; } = string.Empty;
        public string CustomerDisplayProtocolId { get; set; } = string.Empty;
        public string CustomerDisplayFramingId { get; set; } = string.Empty;
        public bool CustomerDisplayDtrRts { get; set; }
        public event EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    [Fact]
    public async Task ConfiguredDisplay_WithNoPortSet_IsSilentAndSucceeds()
    {
        var settings = new FakeSettings { CustomerDisplayPort = string.Empty };
        var display = new ConfiguredCustomerDisplayService(settings);

        Assert.True(await display.ShowTotalAsync(10m));
        Assert.IsType<NullCustomerDisplayService>(display.Inner);
    }

    [Fact]
    public async Task ConfiguredDisplay_PicksUpANewPortWithoutARestart()
    {
        // Иначе после настройки порта кассу пришлось бы перезапускать — тот же
        // приём, что у CompositePrinterService.
        var settings = new FakeSettings { CustomerDisplayPort = string.Empty };
        var display = new ConfiguredCustomerDisplayService(settings);
        Assert.True(await display.ShowTotalAsync(10m));

        // Все три — не дефолты (не 9600, не CP866): подмена Rebuild на
        // захардкоженную конструкцию, забывшую один из трёх параметров, обязана
        // была бы провалить хотя бы одну из проверок ниже, а не остаться зелёной
        // на случайном совпадении со значением по умолчанию.
        settings.CustomerDisplayPort = "COM-does-not-exist";
        settings.CustomerDisplayBaudRate = 2400;
        settings.CustomerDisplayCodePageId = "CP1251";
        settings.Save();

        Assert.False(await display.ShowTotalAsync(10m));

        var vfd = Assert.IsType<VfdDisplayService>(display.Inner);
        Assert.Equal("COM-does-not-exist", vfd.PortName);
        Assert.Equal(2400, vfd.BaudRate);
        Assert.Same(EscPosCodePages.Cp1251, vfd.CodePage);
    }
}
