using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Constants;
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
        public Task<bool> PrintReceiptAsync(IEnumerable<CartItem> i, decimal s, decimal d, decimal t, IEnumerable<Coupon> c, string? discountName = null) => Task.FromResult(true);
        public Task<bool> PrintPreReceiptAsync(IEnumerable<CartItem> i, decimal t) => Task.FromResult(true);
        public Task<bool> OpenCashDrawerAsync() { Drawer++; return Task.FromResult(true); }
        public Task<bool> PrintReturnReceiptAsync(IEnumerable<ReturnReceiptLine> l, decimal t, string d) { Receipt++; return Task.FromResult(true); }
        public Task<bool> PrintExchangeReceiptAsync(IEnumerable<ReturnReceiptLine> returned, IEnumerable<ReturnReceiptLine> issued, decimal difference, string documentNumber) => Task.FromResult(true);
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

    /// <summary>Stands in for CashFeatureService: no storage, flags set directly —
    /// mirrors PosViewModelSellerGateTest's own fake of the same name.</summary>
    private sealed class FakeCashFeatureService : ICashFeatureService
    {
        public CashFeatures Current { get; } = CashFeatures.Default;
        public void Set(string code, bool enabled) => Current.Flags[code] = enabled;
        public Task RefreshAsync() => Task.CompletedTask;
    }

    private static ReturnsViewModel Build(FakeReturnService svc, CountingPrinter printer, FakeSettings settings,
        ICashFeatureService? features = null)
    {
        var vm = new ReturnsViewModel(null, svc, printer, settings, features ?? new FakeCashFeatureService());
        vm.SelectedSale = new ExpenseListItem
        {
            Id = "doc1", DocumentNumber = "9", SelectedDate = "2026-06-06T17:32:55.052Z"
        };
        // after_discount is the whole line's discounted total, so these are 50 and 10
        // a unit respectively — the figures the assertions below are written against.
        vm.Lines.Add(new ReturnLineVm(new ReturnDetailLine
        { Product = new ReturnProduct { Id = "pA" }, Quantity = 3, QuantityReturned = 0, AfterDiscount = 150 }));
        vm.Lines.Add(new ReturnLineVm(new ReturnDetailLine
        { Product = new ReturnProduct { Id = "pB" }, Quantity = 2, QuantityReturned = 0, AfterDiscount = 20 }));
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
    public async Task Submit_NeitherFlagConfigured_BothPostActionsHappen_RegardlessOfLocalSettings()
    {
        var svc = new FakeReturnService();
        var printer = new CountingPrinter();
        // Local checkboxes are both off, but they are no longer consulted at all — an
        // unconfigured flag reads as enabled, same as everywhere else (CashFeatures.IsEnabled),
        // so both post-return actions must still run.
        var settings = new FakeSettings { ReturnOpenCashDrawer = false, ReturnPrintReceipt = false };
        var vm = Build(svc, printer, settings, new FakeCashFeatureService());
        vm.Lines[0].ReturnQty = 1;

        await vm.SubmitReturnCommand.ExecuteAsync(null);

        Assert.Equal("doc1", svc.LastExpenseId);
        Assert.Equal(1, printer.Drawer);
        Assert.Equal(1, printer.Receipt);
    }

    [Fact]
    public async Task Submit_DrawerFlagOff_LocalSettingOn_DoesNotOpenDrawer()
    {
        var svc = new FakeReturnService();
        var printer = new CountingPrinter();
        var settings = new FakeSettings { ReturnOpenCashDrawer = true, ReturnPrintReceipt = true };
        var features = new FakeCashFeatureService();
        features.Set(CashFeatureCodes.ReturnOpenDrawer, false);
        var vm = Build(svc, printer, settings, features);
        vm.Lines[0].ReturnQty = 1;

        await vm.SubmitReturnCommand.ExecuteAsync(null);

        // The store's setting, not the terminal's: a store that switched this off
        // centrally must not have it re-enabled by whatever is ticked on one register.
        Assert.Equal(0, printer.Drawer);
        Assert.Equal(1, printer.Receipt); // the other flag is untouched and still unconfigured -> enabled
    }

    [Fact]
    public async Task Submit_PrintFlagOff_LocalSettingOn_DoesNotPrintReceipt()
    {
        var svc = new FakeReturnService();
        var printer = new CountingPrinter();
        var settings = new FakeSettings { ReturnOpenCashDrawer = true, ReturnPrintReceipt = true };
        var features = new FakeCashFeatureService();
        features.Set(CashFeatureCodes.ReturnPrintReceipt, false);
        var vm = Build(svc, printer, settings, features);
        vm.Lines[0].ReturnQty = 1;

        await vm.SubmitReturnCommand.ExecuteAsync(null);

        // Same rule as the drawer above: the server's answer wins over the local checkbox.
        Assert.Equal(0, printer.Receipt);
        Assert.Equal(1, printer.Drawer); // the other flag is untouched and still unconfigured -> enabled
    }
}
