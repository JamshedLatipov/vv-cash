using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>What CreateReturnAsync reports back — defaults to success (matching
        /// prior behaviour); the HasBookedDocument tests flip it to false.</summary>
        public bool CreateResult = true;

        /// <summary>When set, CreateReturnAsync throws this instead of returning —
        /// mirrors ExchangeViewModelTest's own fake of the same name.</summary>
        public Exception? Throw;

        /// <summary>Every (page, document_number) pair the view model asked for, so the
        /// search tests can prove the typed receipt number actually reached the request
        /// rather than being filtered client-side over a page of unrelated sales.</summary>
        public readonly List<(int Page, string? DocumentNumber)> Queries = new();
        public readonly List<ExpenseListItem> Found = new();

        public Task<ExpenseListResponse> GetSalesAsync(int page = 1, string? documentNumber = null)
        {
            Queries.Add((page, documentNumber));
            return Task.FromResult(new ExpenseListResponse { Body = Found.ToList() });
        }
        public Task<ReturnDetailBody> GetReturnableLinesAsync(string expenseId)
            => Task.FromResult(new ReturnDetailBody());
        public Task<bool> CreateReturnAsync(string expenseId, ReturnRequest request)
        {
            LastExpenseId = expenseId; LastRequest = request;
            if (Throw != null) throw Throw;
            return Task.FromResult(CreateResult);
        }
    }

    private sealed class CountingPrinter : IPrinterService
    {
        public int Drawer; public int Receipt;

        /// <summary>What the last PrintReturnReceiptAsync call actually received in each
        /// slot — WarehouseName/Creator/FormattedSelectedDate are all same-typed
        /// (string?), so a transposition at the call site would compile clean and
        /// pass every other assertion here without this.</summary>
        public string? LastWarehouseName; public string? LastSellerName; public string? LastSaleDate;
        public PrinterStatus Status => PrinterStatus.Ready;
        public event System.EventHandler<PrinterStatus>? StatusChanged;
        public Task<bool> PrintReceiptAsync(IEnumerable<CartItem> i, decimal s, decimal d, decimal t, IEnumerable<Coupon> c, string? discountName = null) => Task.FromResult(true);
        public Task<bool> PrintPreReceiptAsync(IEnumerable<CartItem> i, decimal t) => Task.FromResult(true);
        public Task<bool> OpenCashDrawerAsync() { Drawer++; return Task.FromResult(true); }
        public Task<bool> PrintReturnReceiptAsync(IEnumerable<ReturnReceiptLine> l, decimal t, string d, string? warehouseName = null, string? sellerName = null, string? saleDate = null)
        {
            Receipt++;
            LastWarehouseName = warehouseName; LastSellerName = sellerName; LastSaleDate = saleDate;
            return Task.FromResult(true);
        }
        public Task<bool> PrintExchangeReceiptAsync(IEnumerable<ReturnReceiptLine> returned, IEnumerable<ReturnReceiptLine> issued, decimal difference, string documentNumber, string? warehouseName = null, string? sellerName = null, string? saleDate = null) => Task.FromResult(true);
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
        public string ExchangePayoutCategoryId { get; set; } = string.Empty;
        public string PhoneFormatId { get; set; } = string.Empty;
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
            Id = "doc1", DocumentNumber = "9", SelectedDate = "2026-06-06T17:32:55.052Z",
            WarehouseName = "Central Store", Creator = "Ivanov I."
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

        // Each SelectedSale field must land in ITS OWN printer slot — not a
        // neighboring one. WarehouseName and Creator are deliberately distinct
        // strings above so a transposition between them would fail here.
        Assert.Equal("Central Store", printer.LastWarehouseName);
        Assert.Equal("Ivanov I.", printer.LastSellerName);
        Assert.Equal(vm.SelectedSale!.FormattedSelectedDate, printer.LastSaleDate);
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

    [Fact]
    public async Task SubmitReturn_OnSuccess_MarksHasBookedDocument()
    {
        // PosViewModel reads this after the modal closes to decide whether the register
        // just finished an operation and must re-ask who is selling.
        var svc = new FakeReturnService();
        var vm = Build(svc, new CountingPrinter(), new FakeSettings());
        vm.Lines[0].ReturnQty = 1;

        await vm.SubmitReturnCommand.ExecuteAsync(null);

        Assert.True(vm.HasBookedDocument);
    }

    [Fact]
    public async Task SubmitReturn_WhenServerRejects_LeavesHasBookedDocumentFalse()
    {
        // Nothing was booked, so opening and closing the screen must not cost a PIN.
        var svc = new FakeReturnService { CreateResult = false };
        var vm = Build(svc, new CountingPrinter(), new FakeSettings());
        vm.Lines[0].ReturnQty = 1;

        await vm.SubmitReturnCommand.ExecuteAsync(null);

        Assert.False(vm.HasBookedDocument);
    }

    [Fact]
    public async Task SubmitReturn_WhenServiceThrows_LeavesHasBookedDocumentFalse()
    {
        // A network failure means we don't even know if the server booked it — SubmitReturn
        // catches the exception and reports NoConnection, so the flag must stay unset.
        var svc = new FakeReturnService { Throw = new System.Net.Http.HttpRequestException("connection reset") };
        var vm = Build(svc, new CountingPrinter(), new FakeSettings());
        vm.Lines[0].ReturnQty = 1;

        await vm.SubmitReturnCommand.ExecuteAsync(null);

        Assert.False(vm.HasBookedDocument);
    }

    // ---------------------------------------------------------------------------------
    // Finding the sale by the number on the customer's slip. The list alone only ever
    // showed the server's default page — today's sales — so a return against an older
    // receipt had nothing to select.
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task SearchSales_SendsTheTypedNumberToTheServer()
    {
        var svc = new FakeReturnService();
        var vm = Build(svc, new CountingPrinter(), new FakeSettings());
        svc.Queries.Clear();
        vm.DocumentNumberQuery = "1042";

        await vm.SearchSalesCommand.ExecuteAsync(null);

        var query = Assert.Single(svc.Queries);
        Assert.Equal("1042", query.DocumentNumber);
    }

    [Fact]
    public async Task SearchSales_RestartsAtPageOne()
    {
        // Whatever page the cashier was browsing has nothing to do with where the
        // searched-for receipt lands; asking for page 3 of a one-result search finds
        // nothing and reads as "no such receipt".
        var svc = new FakeReturnService();
        var vm = Build(svc, new CountingPrinter(), new FakeSettings());
        vm.CurrentPage = 3;
        svc.Queries.Clear();
        vm.DocumentNumberQuery = "1042";

        await vm.SearchSalesCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal(1, Assert.Single(svc.Queries).Page);
    }

    [Fact]
    public async Task SearchSales_DropsTheSaleSelectedBefore()
    {
        // The previously selected receipt is not in the new result set, and leaving it
        // selected would leave its lines on screen under a search that did not find it.
        var svc = new FakeReturnService();
        var vm = Build(svc, new CountingPrinter(), new FakeSettings());
        Assert.NotNull(vm.SelectedSale);
        vm.DocumentNumberQuery = "1042";

        await vm.SearchSalesCommand.ExecuteAsync(null);

        Assert.Null(vm.SelectedSale);
    }

    [Fact]
    public async Task ClearSearch_GoesBackToBrowsingWithNoNumber()
    {
        var svc = new FakeReturnService();
        var vm = Build(svc, new CountingPrinter(), new FakeSettings());
        vm.DocumentNumberQuery = "1042";
        await vm.SearchSalesCommand.ExecuteAsync(null);
        svc.Queries.Clear();

        await vm.ClearSearchCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.DocumentNumberQuery);
        Assert.Equal(string.Empty, Assert.Single(svc.Queries).DocumentNumber);
    }

    [Fact]
    public async Task ClearSearch_WithNothingTyped_DoesNotReloadForNothing()
    {
        var svc = new FakeReturnService();
        var vm = Build(svc, new CountingPrinter(), new FakeSettings());
        svc.Queries.Clear();

        await vm.ClearSearchCommand.ExecuteAsync(null);

        Assert.Empty(svc.Queries);
    }
}
