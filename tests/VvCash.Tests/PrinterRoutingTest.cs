using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

/// <summary>Куда какой документ уехал. Без этого набор ролей проверяется только
/// глазами на точке — а ошибка тут выглядит как «кухня молчит», и её ищут в сети.</summary>
public class PrinterRoutingTest
{
    private sealed class RecordingPrinter : EscPosPrinterService
    {
        public List<string> Sent { get; } = new();
        public bool Fails { get; set; }

        public RecordingPrinter(PrinterConfig config)
            : base(config.ConnectionType, config.ConnectionString,
                   EscPosCodePages.Resolve(config.CodePageId), config.Roles) { }

        protected override Task SendAsync(byte[] data)
        {
            if (Fails) throw new InvalidOperationException("printer is on fire");
            Sent.Add(Encoding.Latin1.GetString(data));
            return Task.CompletedTask;
        }
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

    private static (CompositePrinterService Composite, List<RecordingPrinter> Printers)
        Build(params PrintRole[] roles)
    {
        var made = new List<RecordingPrinter>();
        var settings = new FakeSettings
        {
            Printers = roles.Select((r, i) => new PrinterConfig
            {
                Name = $"p{i}",
                ConnectionType = PrinterConnectionType.LAN,
                ConnectionString = $"10.0.0.{i}:9100",
                IsEnabled = true,
                Roles = r
            }).ToList()
        };

        var composite = new CompositePrinterService(settings, config =>
        {
            var p = new RecordingPrinter(config);
            made.Add(p);
            return p;
        });

        return (composite, made);
    }

    private static List<CartItem> OneCoffee() => new()
    {
        new CartItem { Product = new Product { Name = "Coffee", Price = 12m }, Quantity = 2m }
    };

    [Fact]
    public async Task EachDocumentGoesOnlyToPrintersHoldingItsRole()
    {
        var (composite, printers) = Build(
            PrintRole.Receipt,
            PrintRole.Ticket,
            PrintRole.Receipt | PrintRole.KitchenOrder);

        await composite.PrintReceiptAsync(OneCoffee(), 24m, 0m, 24m, Array.Empty<Coupon>());
        await composite.PrintTicketAsync("305", "14:22", "Market 1");
        // Значения, которые не спутать одно с другим: PrintKitchenOrderAsync
        // раскладывает запись в одиннадцать позиционных аргументов, пять подряд
        // string? — переставь любые два, и это соберётся молча.
        await composite.PrintKitchenOrderAsync(new SaleReceiptData(OneCoffee(), 24m, 0m, 24m,
            DocumentNumber: "DOC-77", WarehouseName: "Depot Nine", SellerName: "Zoltan"), "305");

        Assert.Single(printers[0].Sent);
        Assert.Single(printers[1].Sent);
        Assert.Equal(2, printers[2].Sent.Count);
        Assert.Contains("305", printers[1].Sent[0]);
        Assert.Contains("# 305", printers[2].Sent[1]);
        Assert.Contains("DOC-77", printers[2].Sent[1]);
        Assert.Contains("Depot Nine", printers[2].Sent[1]);
        Assert.Contains("Zoltan", printers[2].Sent[1]);
    }

    [Fact]
    public async Task ADeadKitchenPrinterDoesNotFailTheReceipt()
    {
        var (composite, printers) = Build(PrintRole.Receipt, PrintRole.KitchenOrder);
        printers[1].Fails = true;

        var receipt = await composite.PrintReceiptAsync(OneCoffee(), 24m, 0m, 24m, Array.Empty<Coupon>());
        var kitchen = await composite.PrintKitchenOrderAsync(
            new SaleReceiptData(OneCoffee(), 24m, 0m, 24m), "305");

        Assert.True(receipt);
        Assert.False(kitchen);
    }

    [Fact]
    public async Task NoPrinterHoldsTheTicketRole_ReportsFailureRatherThanThrowing()
    {
        var (composite, _) = Build(PrintRole.Receipt);

        Assert.False(await composite.PrintTicketAsync("305"));
    }

    /// <summary>PrinterConfig.Roles обещает, что None гасит принтер, не снимая его
    /// с учёта — но PrintPreReceiptAsync/OpenCashDrawerAsync/PrintReturnReceiptAsync/
    /// PrintExchangeReceiptAsync раньше уходили на весь _printers независимо от
    /// Roles, и None гасил только три документа, маршрутизируемых по ролям. Приколото
    /// здесь, чтобы никто не "починил рассогласование", пустив эти четыре через
    /// For(PrintRole.Receipt) — точка без чекового принтера тогда осталась бы
    /// вовсе без возвратов, что хуже редкого лишнего чека на кухонном аппарате.</summary>
    [Fact]
    public async Task ANoneRolePrinterIsSilencedEntirely_ButOthersStillGetTheSharedDocuments()
    {
        var (composite, printers) = Build(PrintRole.None, PrintRole.KitchenOrder);

        await composite.PrintReceiptAsync(OneCoffee(), 24m, 0m, 24m, Array.Empty<Coupon>());
        await composite.PrintTicketAsync("305");
        await composite.PrintKitchenOrderAsync(new SaleReceiptData(OneCoffee(), 24m, 0m, 24m), "305");
        await composite.PrintPreReceiptAsync(OneCoffee(), 24m);
        await composite.OpenCashDrawerAsync();
        await composite.PrintReturnReceiptAsync(Array.Empty<ReturnReceiptLine>(), 10m, "R-1");
        await composite.PrintExchangeReceiptAsync(Array.Empty<ReturnReceiptLine>(), Array.Empty<ReturnReceiptLine>(), 0m, "E-1");

        Assert.Empty(printers[0].Sent);
        Assert.Contains(printers[1].Sent, s => s.Contains("R-1"));
    }
}
