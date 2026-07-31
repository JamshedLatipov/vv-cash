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

public class ExchangeViewModelTest
{
    // One shared log across all three fakes, so a test can assert on the order the
    // calls actually happened in rather than merely on each having happened.
    private sealed class CallLog
    {
        public readonly List<string> Steps = new();
    }

    private sealed class FakeReturnService : IReturnService
    {
        private readonly CallLog _log;
        public bool Result = true;
        public Exception? Throw;
        public readonly List<ReturnRequest> Requests = new();
        public int ReloadCount;

        public FakeReturnService(CallLog log) => _log = log;

        public Task<ExpenseListResponse> GetSalesAsync(int page = 1) => Task.FromResult(new ExpenseListResponse());

        public Task<ReturnDetailBody> GetReturnableLinesAsync(string expenseId)
        {
            ReloadCount++;
            return Task.FromResult(new ReturnDetailBody());
        }

        public Task<bool> CreateReturnAsync(string expenseId, ReturnRequest request)
        {
            _log.Steps.Add("return");
            Requests.Add(request);
            if (Throw != null) throw Throw;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeCashOperationService : ICashOperationService
    {
        private readonly CallLog _log;
        public CashOpOutcome Outcome = CashOpOutcome.Ok();
        public readonly List<CashExpenseRequest> Requests = new();

        public FakeCashOperationService(CallLog log) => _log = log;

        public Task<CashOpOutcome> CreateCashExpenseAsync(CashExpenseRequest request)
        {
            _log.Steps.Add("payout");
            Requests.Add(request);
            return Task.FromResult(Outcome);
        }
    }

    private sealed class FakeExpenseDocumentService : IExpenseDocumentService
    {
        private readonly CallLog _log;
        public ExpenseDocumentOutcome Outcome = ExpenseDocumentOutcome.Sent("77");
        public Exception? Throw;
        public readonly List<DocumentRequest> Requests = new();

        public FakeExpenseDocumentService(CallLog log) => _log = log;

        public Task<bool> CreateExpenseDocumentAsync(DocumentRequest request)
            => Task.FromResult(true);

        public Task<ExpenseDocumentOutcome> CreateExpenseDocumentDetailedAsync(DocumentRequest request)
        {
            _log.Steps.Add("sale");
            Requests.Add(request);
            if (Throw != null) throw Throw;
            return Task.FromResult(Outcome);
        }

        public Task SyncOfflineDocumentsAsync() => Task.CompletedTask;
        public Task<int> GetUnsyncedDocumentsCountAsync() => Task.FromResult(0);
        public event EventHandler<int>? UnsyncedDocumentsCountChanged { add { } remove { } }
        public event EventHandler? SessionRevoked { add { } remove { } }
    }

    private sealed class FakeCounterpartyService : ICounterpartyService
    {
        public string? SystemId = "cp-system";
        public int Calls;
        public Task<CounterpartyResponse?> CreateCounterpartyAsync(CounterpartyCreateRequest request)
            => Task.FromResult<CounterpartyResponse?>(null);
        public Task<List<CounterpartyResponse>?> SearchCounterpartiesAsync(string query)
            => Task.FromResult<List<CounterpartyResponse>?>(new List<CounterpartyResponse>());
        public Task<string?> GetSystemCounterpartyIdAsync()
        {
            Calls++;
            return Task.FromResult(SystemId);
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
        public string ExchangePayoutCategoryId { get; set; } = "cat-1";
        public string PhoneFormatId { get; set; } = string.Empty;
        public event EventHandler? SettingsChanged { add { } remove { } }
        public void Save() { }
    }

    // Counts calls the same way ReturnsViewModelTest's own CountingPrinter does
    // for a return, plus its own counter for the exchange receipt so the two
    // never get confused.
    private sealed class CountingPrinter : IPrinterService
    {
        public int Drawer;
        public int ExchangeReceipt;
        public decimal? LastDifference;
        public string? LastDocumentNumber;
        public PrinterStatus Status => PrinterStatus.Ready;
        public event System.EventHandler<PrinterStatus>? StatusChanged { add { } remove { } }
        public Task<bool> PrintReceiptAsync(IEnumerable<CartItem> i, decimal s, decimal d, decimal t, IEnumerable<Coupon> c, string? discountName = null) => Task.FromResult(true);
        public Task<bool> PrintPreReceiptAsync(IEnumerable<CartItem> i, decimal t) => Task.FromResult(true);
        public Task<bool> OpenCashDrawerAsync() { Drawer++; return Task.FromResult(true); }
        public Task<bool> PrintReturnReceiptAsync(IEnumerable<ReturnReceiptLine> l, decimal t, string d) => Task.FromResult(true);
        public Task<bool> PrintExchangeReceiptAsync(IEnumerable<ReturnReceiptLine> returned, IEnumerable<ReturnReceiptLine> issued, decimal difference, string documentNumber)
        {
            ExchangeReceipt++;
            LastDifference = difference;
            LastDocumentNumber = documentNumber;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeCashFeatureService : ICashFeatureService
    {
        public CashFeatures Current { get; } = CashFeatures.Default;
        public void Set(string code, bool enabled) => Current.Flags[code] = enabled;
        public Task RefreshAsync() => Task.CompletedTask;
    }

    /// <summary>Everything the submit path touches, wired to one call log.</summary>
    private sealed class Rig
    {
        public readonly CallLog Log = new();
        public FakeReturnService Returns = null!;
        public FakeCashOperationService Payout = null!;
        public FakeExpenseDocumentService Sale = null!;
        public FakeCounterpartyService Counterparties = null!;
        public FakeSettings Settings = null!;
        public CountingPrinter Printer = null!;
        public ExchangeViewModel Vm = null!;
    }

    private static Rig BuildForSubmit(decimal returnedPrice = 80m, decimal issuedPrice = 100m,
        string? payoutCategoryId = "cat-1", string? cashId = "cash-1")
    {
        var rig = new Rig();
        rig.Returns = new FakeReturnService(rig.Log);
        rig.Payout = new FakeCashOperationService(rig.Log);
        rig.Sale = new FakeExpenseDocumentService(rig.Log);
        rig.Counterparties = new FakeCounterpartyService();
        rig.Settings = new FakeSettings { ExchangePayoutCategoryId = payoutCategoryId ?? string.Empty };
        rig.Printer = new CountingPrinter();

        rig.Vm = new ExchangeViewModel(
            returnService: rig.Returns,
            cashOperationService: rig.Payout,
            expenseDocumentService: rig.Sale,
            counterpartyService: rig.Counterparties,
            settingsService: rig.Settings,
            printerService: rig.Printer,
            features: new FakeCashFeatureService(),
            cashId: cashId,
            isOnline: true);

        rig.Vm.SelectedSale = new ExpenseListItem { Id = "doc1", DocumentNumber = "9" };
        rig.Vm.SetReturnedLines(new[] { MakeReturnedLine(returnedPrice) });
        rig.Vm.AddIssuedLine(MakeIssuedLine(issuedPrice));
        return rig;
    }

    [Fact]
    public async Task SubmitExchange_RunsReturnThenPayoutThenSale_InThatOrder()
    {
        // The order is load-bearing, not cosmetic: the processing queue is drained by
        // run_order, so the return has to be in before the sale or exchanging one size
        // for another of the same product drives the remain negative.
        var rig = BuildForSubmit();

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "return", "payout", "sale" }, rig.Log.Steps);
    }

    [Fact]
    public async Task SubmitExchange_PaysOutTheWholeReturnedTotal_AndSellsTheReplacementInFull()
    {
        // The till hands back everything the return was worth and then takes the full
        // price of the replacement; it nets to the difference without either document
        // being anything other than ordinary.
        var rig = BuildForSubmit(returnedPrice: 80m, issuedPrice: 100m);

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        var payout = Assert.Single(rig.Payout.Requests);
        Assert.Equal("expense", payout.OperationType);
        Assert.Equal("cash-1", payout.Cash);
        Assert.Equal("cp-system", payout.Counterparty);
        var detail = Assert.Single(payout.Details);
        Assert.Equal("cat-1", detail.PaymentCategory);
        Assert.Equal(80m, detail.Amount);   // the whole returned total, not the 20 difference

        var sale = Assert.Single(rig.Sale.Requests);
        Assert.Equal(100m, sale.Payment.ToPay);
        Assert.Equal(100m, sale.Payment.PaidInCash);
        Assert.Equal(0m, sale.Payment.Remained);
    }

    [Fact]
    public async Task SubmitExchange_ReturnedGoodsWorthNothing_SkipsThePayoutInsteadOfPostingAZero()
    {
        // A fully discounted line comes back worth nothing. The server binds the payout
        // amount as gt=0, so posting it would be a 400 — with the return already booked
        // — over money that never had to leave the drawer.
        var rig = BuildForSubmit(returnedPrice: 0m, issuedPrice: 100m);

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "return", "sale" }, rig.Log.Steps);
        Assert.Equal(ExchangeViewModel.ExchangeDone, rig.Vm.SuccessMessage);
    }

    [Fact]
    public async Task SubmitExchange_PayoutFails_BasketsIntact_NothingPrinted_MessageNamesWhatWentThrough()
    {
        var rig = BuildForSubmit();
        rig.Payout.Outcome = CashOpOutcome.Failed("cash balance would go negative");

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        // Nothing beyond the payout was attempted.
        Assert.Equal(new[] { "return", "payout" }, rig.Log.Steps);
        Assert.Empty(rig.Sale.Requests);

        // No receipt, no drawer, no success.
        Assert.Equal(0, rig.Printer.ExchangeReceipt);
        Assert.Equal(0, rig.Printer.Drawer);
        Assert.Null(rig.Vm.SuccessMessage);

        // Both baskets exactly as the cashier built them.
        Assert.Single(rig.Vm.IssuedLines);
        Assert.Equal(1, rig.Vm.ReturnedLines.Single().ReturnQty);

        // And the message says which leg is booked and which is not — the only thing
        // that lets the back office find the discrepancy.
        var msg = rig.Vm.ErrorMessage;
        Assert.NotNull(msg);
        Assert.Contains("Возврат проведён", msg);
        Assert.Contains("выдача из кассы не прошла", msg);
        Assert.Contains("продажа не проводилась", msg);
        Assert.Contains("cash balance would go negative", msg);
    }

    [Fact]
    public async Task SubmitExchange_PayoutCategoryUnset_RefusesBeforeAnyCallIsMade()
    {
        var rig = BuildForSubmit(payoutCategoryId: null);

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Empty(rig.Log.Steps);            // nothing was booked, not even the return
        Assert.Equal(0, rig.Counterparties.Calls);
        Assert.Equal(ExchangeViewModel.PayoutCategoryNotConfigured, rig.Vm.ErrorMessage);
        Assert.False(rig.Vm.IsPayoutCategoryConfigured);
    }

    [Fact]
    public async Task SubmitExchange_CashUnknown_RefusesBeforeAnyCallIsMade()
    {
        var rig = BuildForSubmit(cashId: null);

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Empty(rig.Log.Steps);
        Assert.Equal(ExchangeViewModel.CashNotKnown, rig.Vm.ErrorMessage);
    }

    [Fact]
    public async Task SubmitExchange_CounterpartyUnresolved_RefusesBeforeAnyCallIsMade()
    {
        var rig = BuildForSubmit();
        rig.Counterparties.SystemId = null;

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Empty(rig.Log.Steps);
        Assert.Equal(ExchangeViewModel.CounterpartyNotResolved, rig.Vm.ErrorMessage);
    }

    [Fact]
    public async Task SubmitExchange_ReturnFails_NothingElseRuns_AndSaysNothingWasCreated()
    {
        var rig = BuildForSubmit();
        rig.Returns.Result = false;

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "return" }, rig.Log.Steps);
        Assert.Equal(0, rig.Printer.ExchangeReceipt);
        Assert.Null(rig.Vm.SuccessMessage);
        Assert.Contains("Возврат не прошёл", rig.Vm.ErrorMessage);
        Assert.Contains("ни один документ не создан", rig.Vm.ErrorMessage);
    }

    [Fact]
    public async Task SubmitExchange_RetryOfTheSameBaskets_SendsTheSameDocumentHash_AndDoesNotRepeatTheReturn()
    {
        // The dangerous case is a first attempt that commits server-side while its
        // reply is lost, so the cashier presses submit again. A hash minted per press
        // makes that second press a brand new sale for the same goods; the same hash
        // lets the server recognise the duplicate. And the return, which has no undo
        // at all, must not be booked twice either.
        var rig = BuildForSubmit();
        rig.Sale.Throw = new System.Net.Http.HttpRequestException("connection reset"); // the lost reply

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);
        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Single(rig.Returns.Requests);            // booked once; the retry did not repeat it
        Assert.Equal(2, rig.Sale.Requests.Count);
        Assert.NotEmpty(rig.Sale.Requests[0].DocumentHash);
        Assert.Equal(rig.Sale.Requests[0].DocumentHash, rig.Sale.Requests[1].DocumentHash);

        // A changed basket is a different exchange: new key, and a return of its own.
        rig.Vm.AddIssuedLine(MakeIssuedLine(30m));
        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Equal(3, rig.Sale.Requests.Count);
        Assert.NotEqual(rig.Sale.Requests[1].DocumentHash, rig.Sale.Requests[2].DocumentHash);
    }

    [Fact]
    public async Task SubmitExchange_Success_PrintsOnceWithServerDocumentNumber_AndOpensDrawer()
    {
        var rig = BuildForSubmit(returnedPrice: 80m, issuedPrice: 100m);

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Printer.ExchangeReceipt);
        Assert.Equal("77", rig.Printer.LastDocumentNumber); // from the sale's own reply
        Assert.Equal(20m, rig.Printer.LastDifference);
        Assert.Equal(1, rig.Printer.Drawer);               // money actually moved
        Assert.Empty(rig.Vm.IssuedLines);
        Assert.Equal(ExchangeViewModel.ExchangeDone, rig.Vm.SuccessMessage);
    }

    [Fact]
    public async Task SubmitExchange_SaleQueuedOffline_SucceedsButSaysSo()
    {
        // CreateExpenseDocumentDetailedAsync queueing is an acceptable outcome — the
        // document will sync — but claiming the sale went through would send the
        // cashier looking for a document that is not there yet.
        var rig = BuildForSubmit();
        rig.Sale.Outcome = ExpenseDocumentOutcome.Enqueued();

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Printer.ExchangeReceipt);
        Assert.Equal(ExchangeViewModel.ExchangeDoneSaleQueued, rig.Vm.SuccessMessage);
        Assert.Contains("сохранена локально", rig.Vm.SuccessMessage);
        Assert.Null(rig.Vm.ErrorMessage);
    }

    [Fact]
    public async Task SubmitExchange_ExactPriceMatch_PrintsReceipt_ButDoesNotOpenDrawer()
    {
        var rig = BuildForSubmit(returnedPrice: 80m, issuedPrice: 80m);

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Printer.ExchangeReceipt);
        Assert.Equal(0, rig.Printer.Drawer); // nothing for the drawer to hand over or collect
    }

    [Fact]
    public async Task SubmitExchange_ReturnRequestCarriesOnlyTheLinesHandedBack()
    {
        var rig = BuildForSubmit();

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        var req = Assert.Single(rig.Returns.Requests);
        var line = Assert.Single(req.Details);
        Assert.Equal("p1", line.Product);
        Assert.Equal(1, line.Quantity);
        Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), req.SelectedDate);
    }

    // Same shape ReturnsViewModel uses to build a ReturnLineVm: one unit sold,
    // none returned yet, priced at `price` after discount.
    private static ReturnLineVm MakeReturnedLine(decimal price)
    {
        var line = new ReturnLineVm(new ReturnDetailLine
        { Product = new ReturnProduct { Id = "p1" }, Quantity = 1, QuantityReturned = 0, AfterDiscount = price });
        line.ReturnQty = 1;
        return line;
    }

    private static CartItem MakeIssuedLine(decimal price) => new()
    {
        Product = new Product { Id = "p2", Name = "Replacement", Price = price },
        Quantity = 1
    };

    [Fact]
    public void ReplacementDearer_CustomerPaysTheDifference()
    {
        var vm = new ExchangeViewModel();
        vm.SetReturnedLines(new[] { MakeReturnedLine(80m) });
        vm.AddIssuedLine(MakeIssuedLine(100m));

        Assert.Equal(80m, vm.ReturnedTotal);
        Assert.Equal(100m, vm.IssuedTotal);
        Assert.Equal(20m, vm.Difference);
        Assert.True(vm.CustomerPays);
        Assert.False(vm.TillPays);
    }

    [Fact]
    public void ReturnedTotal_LineSoldInMultiples_CreditsOnlyTheUnitsHandedBack()
    {
        // after_discount is the discounted total of the whole sold line, not a unit
        // price: 3 sold for 240 is 80 a unit, so handing one back credits 80. The
        // till payout is that same figure, so reading it per-unit would hand the
        // customer the wrong money out of the drawer.
        var line = new ReturnLineVm(new ReturnDetailLine
        {
            Product = new ReturnProduct { Id = "p1" },
            Quantity = 3, QuantityReturned = 0, AfterDiscount = 240m
        });
        line.ReturnQty = 1;

        var vm = new ExchangeViewModel();
        vm.SetReturnedLines(new[] { line });
        vm.AddIssuedLine(MakeIssuedLine(100m));

        Assert.Equal(80m, vm.ReturnedTotal);
        Assert.Equal(20m, vm.Difference);
        Assert.True(vm.CustomerPays);
        Assert.False(vm.TillPays);
    }

    [Fact]
    public void ReplacementCheaper_TillRefundsTheAbsoluteAmount()
    {
        var vm = new ExchangeViewModel();
        vm.SetReturnedLines(new[] { MakeReturnedLine(100m) });
        vm.AddIssuedLine(MakeIssuedLine(60m));

        Assert.Equal(-40m, vm.Difference);
        Assert.False(vm.CustomerPays);
        Assert.True(vm.TillPays);
        // Shown to the cashier without a minus sign — the label carries the direction.
        Assert.Equal(40m, vm.RefundDue);
    }

    /// <summary>What the backend computes for the replacement sale out of the request
    /// itself (documents.calculateDiscountedPrice, summed into calculated_to_pay):
    /// with discount_type "percent" it takes each line's discount_percent off
    /// sell_price × quantity, with "cash" it subtracts the document-level discount as
    /// money. A plain sale is flagged as suspicious when the two disagree.</summary>
    private static decimal ServerCalculatedTotal(DocumentRequest doc) =>
        doc.Products.Sum(p =>
        {
            var lineTotal = p.SellPrice * p.Quantity;
            return doc.Payment.DiscountType == "percent"
                ? lineTotal - lineTotal * p.DiscountPercent / 100m
                : lineTotal;
        }) - (doc.Payment.DiscountType == "cash" ? doc.Payment.Discount : 0m);

    [Fact]
    public void BuildSaleRequest_IssuedProductWithCatalogDiscount_DeclaredTotalSurvivesTheServersOwnRecalculation()
    {
        // Product.Price is the already-discounted price the screen prices the line at,
        // so the request must not also ask for the catalog percent to be taken off
        // again — the server's total would land below the declared to_pay and the sale
        // would be booked as suspicious.
        var vm = new ExchangeViewModel();
        vm.SetReturnedLines(new[] { MakeReturnedLine(50m) });
        vm.AddIssuedLine(new CartItem
        {
            Product = new Product
            {
                Id = "p9", Name = "Discounted", Price = 80m,
                OriginalPrice = 100m, DiscountPercent = 20m
            },
            Quantity = 2
        });

        var req = vm.BuildSaleRequest();

        Assert.Equal(160m, vm.IssuedTotal);               // what the cashier is shown
        Assert.Equal(vm.IssuedTotal, req.Payment.ToPay);  // and what is declared
        Assert.Equal(req.Payment.ToPay, ServerCalculatedTotal(req));
    }

    [Fact]
    public void CanSubmit_RequiresOnline_AndBothBasketsFilled()
    {
        var vm = new ExchangeViewModel();
        vm.SetReturnedLines(new[] { MakeReturnedLine(80m) });
        vm.AddIssuedLine(MakeIssuedLine(100m));

        vm.IsOnline = false;
        Assert.False(vm.CanSubmit); // offline: neither the return nor the payout can be queued

        vm.IsOnline = true;
        Assert.True(vm.CanSubmit); // baseline: online, both baskets non-empty

        vm.SetReturnedLines(System.Array.Empty<ReturnLineVm>());
        Assert.False(vm.CanSubmit); // nothing selected to return

        vm.SetReturnedLines(new[] { MakeReturnedLine(80m) });
        vm.RemoveIssuedLine(vm.IssuedLines.Single());
        Assert.False(vm.CanSubmit); // nothing selected to issue
    }

    [Fact]
    public async Task SubmitExchange_OnSuccess_MarksHasBookedDocument()
    {
        var rig = BuildForSubmit();

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.True(rig.Vm.HasBookedDocument);
    }

    [Fact]
    public async Task SubmitExchange_ReturnBookedButPayoutFailed_StillMarksHasBookedDocument()
    {
        // The return cannot be cancelled, so a document exists even though the exchange
        // never finished — the register has done something and must re-ask who is selling.
        var rig = BuildForSubmit();
        rig.Payout.Outcome = CashOpOutcome.Failed("cash balance would go negative");

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.NotNull(rig.Vm.ErrorMessage);
        Assert.True(rig.Vm.HasBookedDocument);
    }

    [Fact]
    public async Task SubmitExchange_WhenTheReturnItselfFails_LeavesHasBookedDocumentFalse()
    {
        // Nothing reached the server at all.
        var rig = BuildForSubmit();
        rig.Returns.Result = false;

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.False(rig.Vm.HasBookedDocument);
    }
}
