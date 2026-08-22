using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Hardware;
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
        // набивка колонок его подставить не может в принципе.
        Assert.DoesNotContain("$", VfdDisplayService.BuildTotalFrame(100m));
        Assert.Contains("100.00", VfdDisplayService.BuildTotalFrame(100m));

        Assert.DoesNotContain("$", VfdDisplayService.BuildItemFrame("Молоко", 50m));
        Assert.Contains("50.00", VfdDisplayService.BuildItemFrame("Молоко", 50m));
    }

    [Fact]
    public void Vfd_RendersTwentyColumnsPerLine()
    {
        // 40 и число пробелов ниже привязаны к Columns = 20 в VfdDisplayService.
        // Columns — private const и тесту не виден: если значение изменится, здесь
        // просто перестанет совпадать длина, без подсказки почему.
        var frame = VfdDisplayService.BuildFrame("Молоко", "50.00");

        Assert.Equal(40, frame.Length);
        Assert.StartsWith("Молоко" + new string(' ', 14), frame);
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
        public event EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    [Fact]
    public async Task ConfiguredDisplay_WithNoPortSet_IsSilentAndSucceeds()
    {
        var settings = new FakeSettings { CustomerDisplayPort = string.Empty };
        var display = new ConfiguredCustomerDisplayService(settings);

        Assert.True(await display.ShowTotalAsync(10m));
    }

    [Fact]
    public async Task ConfiguredDisplay_PicksUpANewPortWithoutARestart()
    {
        // Иначе после настройки порта кассу пришлось бы перезапускать — тот же
        // приём, что у CompositePrinterService.
        var settings = new FakeSettings { CustomerDisplayPort = string.Empty };
        var display = new ConfiguredCustomerDisplayService(settings);
        Assert.True(await display.ShowTotalAsync(10m));

        settings.CustomerDisplayPort = "COM-does-not-exist";
        settings.Save();

        Assert.False(await display.ShowTotalAsync(10m));
    }
}
