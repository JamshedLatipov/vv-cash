using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using VvCash.Services.Hardware;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

public class ReturnsViewModelTest
{
    private sealed class FakeReturnService : IReturnService
    {
        public ReturnRequest? LastRequest;
        public string? LastExpenseId;
        public Task<ExpenseListResponse> GetSalesAsync(int page = 1)
            => Task.FromResult(new ExpenseListResponse());
        public Task<ReturnDetailBody> GetReturnableLinesAsync(string expenseId)
            => Task.FromResult(new ReturnDetailBody());
        public Task<bool> CreateReturnAsync(string expenseId, ReturnRequest request)
        {
            LastExpenseId = expenseId; LastRequest = request;
            return Task.FromResult(true);
        }
    }

    private sealed class CountingPrinter : IPrinterService
    {
        public int Drawer; public int Receipt;
        public PrinterStatus Status => PrinterStatus.Ready;
        public event System.EventHandler<PrinterStatus>? StatusChanged;
        public Task<bool> PrintReceiptAsync(IEnumerable<CartItem> i, decimal s, decimal d, decimal t, IEnumerable<Coupon> c) => Task.FromResult(true);
        public Task<bool> PrintPreReceiptAsync(IEnumerable<CartItem> i, decimal t) => Task.FromResult(true);
        public Task<bool> OpenCashDrawerAsync() { Drawer++; return Task.FromResult(true); }
        public Task<bool> PrintReturnReceiptAsync(IEnumerable<ReturnReceiptLine> l, decimal t, string d) { Receipt++; return Task.FromResult(true); }
    }

    private sealed class FakeSettings : ISettingsService
    {
        public string BackendUrl { get; set; } = "https://x/";
        public string CashRegisterToken { get; set; } = "";
        public string AuthToken { get; set; } = "";
        public System.DateTime? AuthTokenExpiresAt { get; set; }
        public int SyncIntervalMinutes { get; set; } = 10;
        public string Language { get; set; } = "ru";
        public List<PrinterConfig> Printers { get; set; } = new();
        public bool ReturnOpenCashDrawer { get; set; } = true;
        public bool ReturnPrintReceipt { get; set; } = true;
        public event System.EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, System.EventArgs.Empty);
    }

    private static ReturnsViewModel Build(FakeReturnService svc, CountingPrinter printer, FakeSettings settings)
    {
        var vm = new ReturnsViewModel(null, svc, printer, settings);
        vm.SelectedSale = new ExpenseListItem
        {
            Id = "doc1", DocumentNumber = "9", SelectedDate = "2026-06-06T17:32:55.052Z"
        };
        vm.Lines.Add(new ReturnLineVm(new ReturnDetailLine
        { Product = new ReturnProduct { Id = "pA" }, Quantity = 3, QuantityReturned = 0, AfterDiscount = 50 }));
        vm.Lines.Add(new ReturnLineVm(new ReturnDetailLine
        { Product = new ReturnProduct { Id = "pB" }, Quantity = 2, QuantityReturned = 0, AfterDiscount = 10 }));
        return vm;
    }

    [Fact]
    public void BuildRequest_OnlyIncludesSelectedLines_WithDateOnly()
    {
        var vm = Build(new FakeReturnService(), new CountingPrinter(), new FakeSettings());
        vm.Lines[0].ReturnQty = 2; // pA only
        var req = vm.BuildRequest();
        Assert.Equal("2026-06-06", req.SelectedDate);
        var d = Assert.Single(req.Details);
        Assert.Equal("pA", d.Product);
        Assert.Equal(2, d.Quantity);
    }

    [Fact]
    public void TotalRefund_SumsSelectedLines()
    {
        var vm = Build(new FakeReturnService(), new CountingPrinter(), new FakeSettings());
        vm.Lines[0].ReturnQty = 2;  // 100
        vm.Lines[1].ReturnQty = 1;  // 10
        Assert.Equal(110m, vm.TotalRefund);
        Assert.True(vm.CanSubmit);
    }

    [Fact]
    public async Task Submit_PostsAndRunsConfiguredPostActions()
    {
        var svc = new FakeReturnService();
        var printer = new CountingPrinter();
        var vm = Build(svc, printer, new FakeSettings());
        vm.Lines[0].ReturnQty = 1;

        await vm.SubmitReturnCommand.ExecuteAsync(null);

        Assert.Equal("doc1", svc.LastExpenseId);
        Assert.Equal(1, printer.Drawer);
        Assert.Equal(1, printer.Receipt);
    }

    [Fact]
    public async Task Submit_RespectsDisabledPostActions()
    {
        var svc = new FakeReturnService();
        var printer = new CountingPrinter();
        var settings = new FakeSettings { ReturnOpenCashDrawer = false, ReturnPrintReceipt = false };
        var vm = Build(svc, printer, settings);
        vm.Lines[0].ReturnQty = 1;

        await vm.SubmitReturnCommand.ExecuteAsync(null);

        Assert.Equal(0, printer.Drawer);
        Assert.Equal(0, printer.Receipt);
    }
}
