using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Models;
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
        public event EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Печатает мгновенно: ConnectionType нарочно вне диапазона enum, поэтому
    /// SendAsync уходит в свою ветку default: и возвращается без единого байта
    /// ввода-вывода. Почему это оправдано именно здесь — в комментарии теста ниже.</summary>
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
        // Транспорт здесь намеренно не сетевой. Первая редакция этого теста печатала
        // на закрытый порт loopback и не воспроизвела гонку ни разу за три прогона по
        // 6m46s/6m47s/6m48s: TcpClient.Connect на этой машине отказывает не сразу, а
        // примерно за 2.2 секунды (замерено отдельно, напрямую), и цикл перенастройки
        // успевал отработать все 200 итераций Clear()/Add(), пока цикл печати ещё
        // стоял на первом же await ConnectAsync. Окно перекрытия, от которого зависел
        // тест, схлопывалось в ноль что до фикса, что после — тест был зелёным при
        // любом порядке дел, три раза из трёх, и не поймал бы регресс никогда.
        //
        // (PrinterConnectionType)99 — нарочно вне диапазона enum, чтобы SendAsync ушёл
        // в собственную ветку default: и вернулся без единого байта ввода-вывода.
        // Предмет теста — подмена списка _printers, а не транспорт; гонять здесь
        // настоящий сокет так же не по адресу, как гонять настоящий принтер.
        //
        // Без сети гонка ловится с первой попытки за десятки миллисекунд. Симптом на
        // .NET 10 — не классический InvalidOperationException: Collection was
        // modified, а NullReferenceException из ListSelectIterator.Fill: быстрый путь
        // List<T>.Select().ToList() читает внутренний массив по индексу напрямую, без
        // версии и без MoveNext(), и конкурентный Clear() успевает обнулить элемент
        // между двумя чтениями. Корень тот же самый — другое исключение здесь не
        // значит, что тест сгнил.
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

        Assert.Same(EscPosCodePages.Cp1251, composite.Printers[0].CodePage);
        Assert.Same(EscPosCodePages.Default, composite.Printers[1].CodePage);
    }
}
