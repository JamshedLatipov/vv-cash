using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Models.Receipt;
using VvCash.Services;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

/// <summary>Пересборка состава принтеров по SettingsChanged, пока печать идёт.
/// Кнопка пробной печати делает связку «поменял настройку → сразу печатаю»
/// обычным сценарием, а не редким.</summary>
public class CompositePrinterServiceTest
{
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

    /// <summary>Печатает мгновенно: ConnectionType нарочно вне диапазона enum, поэтому
    /// SendAsync уходит в свою ветку default: и бросает NotSupportedException, не
    /// тронув транспорт ни на байт. PrintPreReceiptAsync ловит это тем же catch, что
    /// и любой другой отказ транспорта, и возвращает false — тесту ниже нужно было
    /// не «возвращается», а «не делает ввода-вывода», и это свойство осталось.
    /// address в этой ветке не читается вовсе: он существует не ради маршрута, а
    /// чтобы разные поколения принтеров были различимы под отладчиком. Почему сам
    /// приём с ConnectionType оправдан здесь — в комментарии теста ниже.</summary>
    private static PrinterConfig Fast(string address) => new()
    {
        Name = address,
        ConnectionType = (PrinterConnectionType)99,
        ConnectionString = address,
        IsEnabled = true
    };

    [Fact]
    public async Task PrintingSurvivesASettingsChangeMidFlight()
    {
        // Транспорт не сетевой — намеренно. Печать на закрытый порт loopback здесь
        // отказывает не сразу, а за ~2.2с, и цикл перенастройки успевает отработать
        // всё, пока печать стоит на первой попытке: окно перекрытия схлопывается в
        // ноль, и первая редакция была зелёной что до фикса, что после.
        // (PrinterConnectionType)99 нарочно вне диапазона enum — уводит SendAsync в
        // default: без единого байта ввода-вывода, и гонка ловится с первой попытки
        // за десятки миллисекунд. Симптом на .NET 10 — не классический
        // InvalidOperationException, а NullReferenceException из ListSelectIterator.Fill
        // (Clear() успевает обнулить элемент внутреннего массива между чтениями
        // List<T>.Select()); корень тот же — другое исключение тут не значит, что
        // тест сгнил.
        var settings = new FakeSettings { Printers = { Fast("x1") } };
        var composite = new CompositePrinterService(settings);

        var printing = Task.Run(async () =>
        {
            for (var i = 0; i < 20000; i++)
            {
                await composite.PrintPreReceiptAsync(Array.Empty<CartItem>(), 0m);
            }
        });

        var reconfiguring = Task.Run(() =>
        {
            for (var i = 0; i < 20000; i++)
            {
                settings.Printers = new List<PrinterConfig> { Fast($"x{2 + (i % 3)}") };
                settings.Save();
            }
        });

        await Task.WhenAll(printing, reconfiguring);
    }

    [Fact]
    public async Task NoPrintersConfigured_ReportsFailureRatherThanThrowing()
    {
        var composite = new CompositePrinterService(new FakeSettings());

        Assert.False(await composite.PrintPreReceiptAsync(Array.Empty<CartItem>(), 0m));
        Assert.False(await composite.OpenCashDrawerAsync());
    }

    [Fact]
    public void EachPrinterGetsTheCodePageFromItsOwnConfig()
    {
        var settings = new FakeSettings
        {
            Printers =
            {
                new() { Name = "a", ConnectionType = PrinterConnectionType.LAN,
                        ConnectionString = "10.0.0.1:9100", IsEnabled = true, CodePageId = "CP1251" },
                new() { Name = "b", ConnectionType = PrinterConnectionType.LAN,
                        ConnectionString = "10.0.0.2:9100", IsEnabled = true, CodePageId = "" }
            }
        };

        var composite = new CompositePrinterService(settings);

        // Индексы 0/1 совпадают с порядком конфигурации только потому, что
        // Where(IsEnabled) сохраняет исходный порядок; отключённый принтер между
        // ними сдвинул бы их.
        Assert.Same(EscPosCodePages.Cp1251, composite.Printers[0].CodePage);
        Assert.Same(EscPosCodePages.Default, composite.Printers[1].CodePage);
    }

    /// <summary>Тот же пробел, что и с кодовой страницей выше, только для ролей:
    /// PrinterRoutingTest всегда подставляет свою фабрику и до умолчательной не
    /// доходит. Убери config.Roles из фабрики по умолчанию — и это единственное,
    /// что заметит подмену.</summary>
    [Fact]
    public void EachPrinterGetsTheRolesFromItsOwnConfig()
    {
        var settings = new FakeSettings
        {
            Printers =
            {
                new() { Name = "a", ConnectionType = PrinterConnectionType.LAN,
                        ConnectionString = "10.0.0.1:9100", IsEnabled = true,
                        Roles = PrintRole.Ticket | PrintRole.KitchenOrder }
            }
        };

        var composite = new CompositePrinterService(settings);

        Assert.Equal(PrintRole.Ticket | PrintRole.KitchenOrder, composite.Printers[0].Roles);
    }

    /// <summary>Тот же пробел, что и с кодовой страницей и ролями выше, только для
    /// шаблона: фабрика по умолчанию — единственный код, который реально отдаёт
    /// поставщика шаблона каждому принтеру, и никакой другой тест до неё не
    /// доходит — PrinterRoutingTest всегда подставляет свою фабрику. Убери
    /// проброс template из фабрики по умолчанию в CompositePrinterService — и
    /// это единственное, что заметит пропажу: остальные 1089 тестов останутся
    /// зелёными, потому что ни один принтер, собранный где-либо ещё в наборе,
    /// не получает шаблон отдельно от этой строки.
    ///
    /// template: именованным параметром и без printerFactory — иначе подмена
    /// фабрики (как везде в этом файле и в PrinterRoutingTest) обошла бы именно
    /// ту строку, которую тест обязан проверить.</summary>
    [Fact]
    public void EachPrinterReadsTheSameLiveTemplate_FromTheSharedProviderOfTheDefaultFactory()
    {
        var settings = new FakeSettings
        {
            Printers =
            {
                new() { Name = "a", ConnectionType = PrinterConnectionType.LAN,
                        ConnectionString = "10.0.0.1:9100", IsEnabled = true },
                new() { Name = "b", ConnectionType = PrinterConnectionType.LAN,
                        ConnectionString = "10.0.0.2:9100", IsEnabled = true },
            }
        };

        var current = ReceiptTemplate.Default;
        var composite = new CompositePrinterService(settings, template: () => (current, ""));

        // Меняем шаблон ПОСЛЕ сборки состава — свойство, ради которого поставщик
        // вообще заведён: состав принтеров не пересобирается на смену шаблона, а
        // каждый принтер обязан увидеть новое значение на следующей же печати.
        current = new ReceiptTemplate
        {
            Width = 32,
            Blocks = new List<ReceiptBlock> { new TextBlock { Content = "ЖИВОЙ ШАБЛОН" } },
        };

        Assert.Equal(2, composite.Printers.Count);
        Assert.All(composite.Printers, p => Assert.Contains("ЖИВОЙ ШАБЛОН",
            p.CodePage.Encoding.GetString(p.BuildConfiguredSaleReceipt(new List<CartItem>(), 0m, 0m, 0m))));
    }
}
