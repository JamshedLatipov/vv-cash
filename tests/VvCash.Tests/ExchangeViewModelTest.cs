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

        /// <summary>What a receipt-number lookup finds. Empty by default — the tests below
        /// set SelectedSale directly rather than going through the search.</summary>
        public readonly List<ExpenseListItem> Found = new();
        public readonly List<string?> SearchedNumbers = new();

        public Task<ExpenseListResponse> GetSalesAsync(int page = 1, string? documentNumber = null)
        {
            SearchedNumbers.Add(documentNumber);
            return Task.FromResult(new ExpenseListResponse { Body = Found.ToList() });
        }

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
        public string ReturnPayoutCategoryId { get; set; } = string.Empty;
        public string PhoneFormatId { get; set; } = string.Empty;
        public string CustomerDisplayPort { get; set; } = string.Empty;
        public int CustomerDisplayBaudRate { get; set; } = 9600;
        public string CustomerDisplayCodePageId { get; set; } = string.Empty;
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
        public List<ReturnReceiptLine>? LastIssuedLines;

        /// <summary>What the last PrintExchangeReceiptAsync call actually received in each
        /// slot — WarehouseName/Creator/FormattedSelectedDate are all same-typed
        /// (string?), so a transposition at the call site would compile clean and
        /// pass every other assertion here without this.</summary>
        public string? LastWarehouseName; public string? LastSellerName; public string? LastSaleDate;
        public PrinterStatus Status => PrinterStatus.Ready;
        public event System.EventHandler<PrinterStatus>? StatusChanged { add { } remove { } }
        public Task<bool> PrintReceiptAsync(IEnumerable<CartItem> i, decimal s, decimal d, decimal t, IEnumerable<Coupon> c, string? discountName = null,
            string? documentNumber = null, string? warehouseName = null, string? sellerName = null, string? saleDate = null) => Task.FromResult(true);
        public Task<bool> PrintPreReceiptAsync(IEnumerable<CartItem> i, decimal t) => Task.FromResult(true);
        public Task<bool> OpenCashDrawerAsync() { Drawer++; return Task.FromResult(true); }
        public Task<bool> PrintReturnReceiptAsync(IEnumerable<ReturnReceiptLine> l, decimal t, string d, string? warehouseName = null, string? sellerName = null, string? saleDate = null) => Task.FromResult(true);
        public Task<bool> PrintExchangeReceiptAsync(IEnumerable<ReturnReceiptLine> returned, IEnumerable<ReturnReceiptLine> issued, decimal difference, string documentNumber, string? warehouseName = null, string? sellerName = null, string? saleDate = null)
        {
            ExchangeReceipt++;
            LastDifference = difference;
            LastDocumentNumber = documentNumber;
            LastIssuedLines = issued.ToList();
            LastWarehouseName = warehouseName; LastSellerName = sellerName; LastSaleDate = saleDate;
            return Task.FromResult(true);
        }
        public Task<bool> PrintTicketAsync(string number, string? time = null, string? warehouseName = null)
            => Task.FromResult(true);
        public Task<bool> PrintKitchenOrderAsync(SaleReceiptData sale, string queueNumber)
            => Task.FromResult(true);
    }

    private sealed class FakeCashFeatureService : ICashFeatureService
    {
        public CashFeatures Current { get; } = CashFeatures.Default;
        public bool HasLoaded => true;
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
        public FakeQuoteService Quotes = null!;
        public ExchangeViewModel Vm = null!;
    }

    /// <param name="quote">What the server prices the issued basket at. Null — the
    /// default — is the unpriced case every test written before this screen quoted at all
    /// relies on: no discount, so the issued total stays the catalog one.</param>
    private static Rig BuildForSubmit(decimal returnedPrice = 80m, decimal issuedPrice = 100m,
        string? payoutCategoryId = "cat-1", string? cashId = "cash-1", QuoteResult? quote = null)
    {
        var rig = new Rig();
        rig.Returns = new FakeReturnService(rig.Log);
        rig.Payout = new FakeCashOperationService(rig.Log);
        rig.Sale = new FakeExpenseDocumentService(rig.Log);
        rig.Counterparties = new FakeCounterpartyService();
        rig.Settings = new FakeSettings { ExchangePayoutCategoryId = payoutCategoryId ?? string.Empty };
        rig.Printer = new CountingPrinter();
        rig.Quotes = new FakeQuoteService { Result = quote };

        rig.Vm = new ExchangeViewModel(
            returnService: rig.Returns,
            cashOperationService: rig.Payout,
            expenseDocumentService: rig.Sale,
            counterpartyService: rig.Counterparties,
            settingsService: rig.Settings,
            printerService: rig.Printer,
            features: new FakeCashFeatureService(),
            quoteService: rig.Quotes,
            cashId: cashId,
            isOnline: true);

        rig.Vm.SelectedSale = new ExpenseListItem
        {
            Id = "doc1", DocumentNumber = "9", SelectedDate = "2026-06-06T17:32:55.052Z",
            WarehouseName = "Central Store", Creator = "Ivanov I."
        };
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

        // Each SelectedSale field must land in ITS OWN printer slot — not a
        // neighboring one. WarehouseName and Creator are deliberately distinct
        // strings in BuildForSubmit so a transposition between them would fail here.
        Assert.Equal("Central Store", rig.Printer.LastWarehouseName);
        Assert.Equal("Ivanov I.", rig.Printer.LastSellerName);
        Assert.Equal(rig.Vm.SelectedSale!.FormattedSelectedDate, rig.Printer.LastSaleDate);
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
        Assert.Single(rig.Payout.Requests);             // and the till paid out once
        Assert.Equal(2, rig.Sale.Requests.Count);
        Assert.NotEmpty(rig.Sale.Requests[0].DocumentHash);
        Assert.Equal(rig.Sale.Requests[0].DocumentHash, rig.Sale.Requests[1].DocumentHash);

        // A changed REPLACEMENT basket is a different sale — new hash — but it is the same
        // goods coming back, so the return and its payout stay booked exactly once. They
        // are built from the returned basket, which nothing here touched.
        rig.Vm.AddIssuedLine(MakeIssuedLine(30m));
        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Equal(3, rig.Sale.Requests.Count);
        Assert.NotEqual(rig.Sale.Requests[1].DocumentHash, rig.Sale.Requests[2].DocumentHash);
        Assert.Single(rig.Returns.Requests);
        Assert.Single(rig.Payout.Requests);
    }

    [Fact]
    public async Task SubmitExchange_RetryAfterAFailedPayout_DoesNotPayOutTwice()
    {
        // The payout is real money leaving the drawer and CashOperationService has no
        // cancel endpoint. A retry after the sale leg failed must not post a second one:
        // the books would say 160 left the till for an 80 return.
        var rig = BuildForSubmit(returnedPrice: 80m, issuedPrice: 100m);
        rig.Sale.Throw = new System.Net.Http.HttpRequestException("connection reset");

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);
        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Single(rig.Payout.Requests);
        Assert.Equal(80m, rig.Payout.Requests[0].Details[0].Amount);
    }

    [Fact]
    public async Task SubmitExchange_EditingTheIssuedBasketAfterAPartialFailure_DoesNotBookTheReturnAgain()
    {
        // Leg 1 committed, leg 2 refused. The cashier swaps the replacement for a cheaper
        // one — an edit to the ISSUED basket, which is no part of the return document.
        // Re-posting the return credits the same goods to stock twice, and there is no
        // endpoint that cancels a return.
        var rig = BuildForSubmit(returnedPrice: 80m, issuedPrice: 100m);
        rig.Payout.Outcome = CashOpOutcome.Failed("cash balance would go negative");

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);
        Assert.Single(rig.Returns.Requests);

        rig.Vm.AddIssuedLine(MakeIssuedLine(60m));
        rig.Payout.Outcome = CashOpOutcome.Ok();
        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        // Still exactly one return: it committed, and nothing about the issued basket
        // makes it a different one.
        Assert.Single(rig.Returns.Requests);
        // The payout, by contrast, is attempted twice on purpose — the first attempt was
        // refused, so no money left the drawer and it genuinely has to be retried.
        Assert.Equal(2, rig.Payout.Requests.Count);
    }

    [Fact]
    public async Task SubmitExchange_EditingTheReturnedBasket_DoesBookANewReturn()
    {
        // The other half of the same rule: the returned basket IS what the return document
        // is built from, so changing it is a genuinely different return.
        var rig = BuildForSubmit(returnedPrice: 80m, issuedPrice: 100m);
        rig.Payout.Outcome = CashOpOutcome.Failed("cash balance would go negative");

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);
        Assert.Single(rig.Returns.Requests);

        rig.Vm.SetReturnedLines(new[] { MakeReturnedLine(40m) });
        rig.Vm.ReturnedLines[0].ReturnQty = 1;
        rig.Payout.Outcome = CashOpOutcome.Ok();
        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Equal(2, rig.Returns.Requests.Count);
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

    // -------------------------------------------------------------------------------
    // How the replacement sale is tendered. Step 2 hands the whole returned total out
    // of the till and step 3 takes the replacement's full price back, so the drawer
    // nets to the difference — but only if the sale says which way that money moved.
    // -------------------------------------------------------------------------------

    [Fact]
    public void BuildSaleRequest_DefaultsToCash()
    {
        var vm = new ExchangeViewModel();
        vm.SetReturnedLines(new[] { MakeReturnedLine(50m) });
        vm.AddIssuedLine(MakeIssuedLine(120m));

        var req = vm.BuildSaleRequest();

        Assert.Equal(120m, req.Payment.PaidInCash);
        Assert.Equal(0m, req.Payment.PaidByCreditCard);
        Assert.Equal(0m, req.Payment.Remained);
    }

    [Fact]
    public void BuildSaleRequest_PaidByCard_SplitsTheTenderTheWayTheMoneyActuallyMoved()
    {
        // The checkbox says the DIFFERENCE is paid by card. Step 2 has already handed the
        // returned total out of the drawer, and the customer hands that same cash straight
        // back for the replacement — only the difference goes on the terminal. Booking the
        // whole replacement to the card slot leaves the drawer over by the returned total
        // and the terminal short by it, which is a bigger error than the all-cash version
        // this option replaced.
        var vm = new ExchangeViewModel();
        vm.SetReturnedLines(new[] { MakeReturnedLine(80m) });
        vm.AddIssuedLine(MakeIssuedLine(100m));

        vm.PayByCard = true;
        var req = vm.BuildSaleRequest();

        Assert.Equal(80m, req.Payment.PaidInCash);        // the returned money, back in
        Assert.Equal(20m, req.Payment.PaidByCreditCard);  // the difference, on the terminal
        Assert.Equal(100m, req.Payment.ToPay);
        Assert.Equal(req.Payment.ToPay, req.Payment.PaidInCash + req.Payment.PaidByCreditCard);
        Assert.Equal(0m, req.Payment.Remained);
    }

    [Fact]
    public void PayByCard_IsOnlyOfferedWhenTheCustomerOwesSomething()
    {
        // Nothing to put on a card when the till is the one paying out: the refund leg
        // is the cash payout of step 2, and the replacement is worth less than what came
        // back.
        var vm = new ExchangeViewModel();
        vm.SetReturnedLines(new[] { MakeReturnedLine(200m) });
        vm.AddIssuedLine(MakeIssuedLine(120m));

        Assert.True(vm.TillPays);
        Assert.False(vm.CanPayByCard);
    }

    [Fact]
    public void PayByCard_IsOfferedWhenTheCustomerOwesTheDifference()
    {
        var vm = new ExchangeViewModel();
        vm.SetReturnedLines(new[] { MakeReturnedLine(50m) });
        vm.AddIssuedLine(MakeIssuedLine(120m));

        Assert.True(vm.CustomerPays);
        Assert.True(vm.CanPayByCard);
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

    // ---------------------------------------------------------------------------------
    // Pricing the replacement goods. The bug: this screen priced them straight off the
    // register's cached catalog, so a shirt bought under a running -50% promotion cost
    // its full 100 back when the customer came to exchange it for the same shirt —
    // the promotion was still on, the exchange just never asked the server about it.
    // ---------------------------------------------------------------------------------

    private sealed class FakeQuoteService : IQuoteService
    {
        public readonly List<QuoteRequest> Requests = new();
        public QuoteResult? Result;

        /// <summary>Every await in this file's fakes completes synchronously so the view
        /// model's fire-and-forget requote has finished by the time the caller's next line
        /// runs — the same deterministic-fakes trick PosViewModelSellerGateTest documents.</summary>
        public Task<QuoteResult?> QuoteAsync(QuoteRequest request, System.Threading.CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }

    /// <summary>A server reply that takes <paramref name="percent"/> off a single line of
    /// <paramref name="unitPrice"/>. unit_price echoes the price BEFORE the discount —
    /// that is what the real endpoint returns (discounts/quote.go), and reading it as the
    /// discounted price is what would make the register declare the discount twice.</summary>
    private static QuoteResult MakeQuote(string productId, decimal unitPrice, decimal quantity, decimal percent)
    {
        var subtotal = unitPrice * quantity;
        var discount = subtotal * percent / 100m;
        return new QuoteResult
        {
            QuoteId = "q-1",
            Subtotal = subtotal,
            DiscountTotal = discount,
            Total = subtotal - discount,
            Lines =
            {
                new QuoteLineResult
                {
                    ProductId = productId,
                    Quantity = quantity,
                    UnitPrice = unitPrice,
                    LineSubtotal = subtotal,
                    DiscountAmount = discount,
                    DiscountPercent = percent,
                    FinalLineTotal = subtotal - discount,
                },
            },
        };
    }

    private static (ExchangeViewModel vm, FakeQuoteService quotes) BuildForPricing()
    {
        var quotes = new FakeQuoteService();
        var vm = new ExchangeViewModel(quoteService: quotes, isOnline: true);
        return (vm, quotes);
    }

    [Fact]
    public void IssuedBasket_ServerDiscountsIt_TotalIsTheDiscountedOne()
    {
        // The reported bug, as arithmetic: a 100 shirt under a running -50% promotion is
        // 50 to issue, not 100 — and the customer who handed back the same shirt they
        // bought for 50 owes nothing.
        var (vm, quotes) = BuildForPricing();
        quotes.Result = MakeQuote("p2", unitPrice: 100m, quantity: 1m, percent: 50m);
        vm.SetReturnedLines(new[] { MakeReturnedLine(50m) });

        vm.AddIssuedLine(MakeIssuedLine(100m));

        Assert.Equal(100m, vm.IssuedSubtotal);
        Assert.Equal(50m, vm.IssuedDiscount);
        Assert.True(vm.HasIssuedDiscount);
        Assert.Equal(50m, vm.IssuedTotal);
        Assert.Equal(0m, vm.Difference);
    }

    [Fact]
    public void IssuedBasket_NoQuote_FallsBackToCatalogPricing()
    {
        // A failed or absent quote must never block the exchange — the basket just
        // prices locally, exactly as the POS cart does. Nothing is discounted, so the
        // discount row stays off the screen rather than showing a zero.
        var (vm, quotes) = BuildForPricing();
        quotes.Result = null;

        vm.AddIssuedLine(MakeIssuedLine(100m));

        Assert.Equal(100m, vm.IssuedSubtotal);
        Assert.Equal(0m, vm.IssuedDiscount);
        Assert.False(vm.HasIssuedDiscount);
        Assert.Equal(100m, vm.IssuedTotal);
    }

    [Fact]
    public void IssuedBasket_Offline_IsNotQuotedAtAll()
    {
        // Nothing to ask and nothing to ask it of. CanSubmit already refuses offline, so
        // this only has to avoid a pointless round trip, not price anything.
        var quotes = new FakeQuoteService();
        var vm = new ExchangeViewModel(quoteService: quotes, isOnline: false);

        vm.AddIssuedLine(MakeIssuedLine(100m));

        Assert.Empty(quotes.Requests);
        Assert.Equal(100m, vm.IssuedTotal);
    }

    [Fact]
    public void IssuedBasket_ServerDiscountsIt_TheLineItselfShowsWhatCameOffIt()
    {
        // The totals block alone said a discount existed but not which of the replacement
        // goods it came off, so every card still read at the catalog price. The line now
        // carries the quote's own figures, which is what the card prints.
        var (vm, quotes) = BuildForPricing();
        quotes.Result = MakeQuote("p2", unitPrice: 100m, quantity: 1m, percent: 50m);
        var line = MakeIssuedLine(100m);

        vm.AddIssuedLine(line);

        Assert.True(line.HasLineDiscount);
        Assert.Equal(50m, line.LineDiscount);
        Assert.Equal(50m, line.QuotedDiscountPercent);
        Assert.Equal(100m, line.LineTotal);      // struck through on screen
        Assert.Equal(50m, line.LineFinalTotal);  // what the customer is charged for it
    }

    [Fact]
    public void IssuedBasket_NoQuote_LineShowsNoDiscountAtAll()
    {
        // Catalog fallback: nothing was priced, so nothing may claim to have been
        // discounted — a zero-percent badge on every card would be worse than none.
        var (vm, quotes) = BuildForPricing();
        quotes.Result = null;
        var line = MakeIssuedLine(100m);

        vm.AddIssuedLine(line);

        Assert.False(line.HasLineDiscount);
        Assert.Equal(0m, line.LineDiscount);
        Assert.Equal(100m, line.LineFinalTotal);
    }

    [Fact]
    public void IssuedBasket_EmptiedAgain_DropsTheDiscountWithIt()
    {
        // The discount belongs to a basket, not to the screen: removing the goods it
        // applied to must not leave the figure behind, or the next basket inherits a
        // discount nobody granted it.
        var (vm, quotes) = BuildForPricing();
        quotes.Result = MakeQuote("p2", unitPrice: 100m, quantity: 1m, percent: 50m);
        var line = MakeIssuedLine(100m);
        vm.AddIssuedLine(line);
        Assert.Equal(50m, vm.IssuedDiscount);

        vm.RemoveIssuedLine(line);

        Assert.Equal(0m, vm.IssuedDiscount);
        Assert.False(vm.HasIssuedDiscount);
        Assert.Equal(0m, vm.IssuedTotal);
    }

    [Fact]
    public async Task SubmitExchange_DiscountedReplacement_DeclaresTheDiscountInMoneyOnce()
    {
        // How the sale has to reach the server: sell_price is the price BEFORE the
        // discount and the discount is declared once, document-level, as cash. Sending
        // the discounted price here *and* the discount would have the server subtract it
        // a second time and book the line at zero.
        var rig = BuildForSubmit(returnedPrice: 50m, issuedPrice: 100m,
            quote: MakeQuote("p2", unitPrice: 100m, quantity: 1m, percent: 50m));

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        var sale = Assert.Single(rig.Sale.Requests);
        Assert.Equal(50m, sale.Payment.ToPay);
        Assert.Equal(50m, sale.Payment.PaidInCash);
        Assert.Equal("cash", sale.Payment.DiscountType);
        Assert.Equal(50m, sale.Payment.Discount);
        Assert.Equal("q-1", sale.QuoteId);

        var product = Assert.Single(sale.Products);
        Assert.Equal(100m, product.SellPrice);            // before the discount
        Assert.Equal(100m, product.PriceBeforeDiscount);
        Assert.Equal(50m, product.DiscountPercent);
    }

    // ---------------------------------------------------------------------------------
    // Finding the receipt. This screen used to open on a browsable page of every sale
    // the register had rung, and it pays the whole returned total out of the till — so
    // a cashier could pick an arbitrary receipt and move drawer money against it. The
    // list is gone; a number off the customer's slip is the only way in.
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task SearchSale_FoundByNumber_SelectsItAndLoadsItsLines()
    {
        var log = new CallLog();
        var returns = new FakeReturnService(log);
        returns.Found.Add(new ExpenseListItem { Id = "doc1", DocumentNumber = "1042" });
        var vm = new ExchangeViewModel(returnService: returns, isOnline: true)
        {
            DocumentNumberQuery = "1042",
        };

        await vm.SearchSaleCommand.ExecuteAsync(null);

        Assert.NotNull(vm.SelectedSale);
        Assert.Equal("1042", vm.SelectedSale!.DocumentNumber);
        Assert.Equal("1042", Assert.Single(returns.SearchedNumbers));
        Assert.Equal(1, returns.ReloadCount);  // selecting it loaded the returnable lines
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task SearchSale_NoSuchNumber_SaysSoAndSelectsNothing()
    {
        var returns = new FakeReturnService(new CallLog());  // Found stays empty
        var vm = new ExchangeViewModel(returnService: returns, isOnline: true)
        {
            DocumentNumberQuery = "9999",
        };

        await vm.SearchSaleCommand.ExecuteAsync(null);

        Assert.Null(vm.SelectedSale);
        Assert.False(vm.HasSelectedSale);
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task SearchSale_BlankNumber_AsksTheServerForNothingAtAll()
    {
        // Sending a blank through would ask for an unfiltered page — the browsable list
        // this screen exists without. Nothing may leave the register for an empty box.
        var returns = new FakeReturnService(new CallLog());
        var vm = new ExchangeViewModel(returnService: returns, isOnline: true)
        {
            DocumentNumberQuery = "   ",
        };

        await vm.SearchSaleCommand.ExecuteAsync(null);

        Assert.Empty(returns.SearchedNumbers);
        Assert.Null(vm.SelectedSale);
    }

    [Fact]
    public async Task SearchSale_ForAnotherReceipt_DropsTheIssuedBasketBuiltForThePreviousOne()
    {
        // The replacement goods belong to the exchange that was being built, not to the
        // screen. Carrying them over would price a new receipt's exchange against a
        // basket assembled for a different customer.
        var returns = new FakeReturnService(new CallLog());
        returns.Found.Add(new ExpenseListItem { Id = "doc1", DocumentNumber = "1042" });
        var vm = new ExchangeViewModel(returnService: returns, isOnline: true)
        {
            DocumentNumberQuery = "1042",
        };
        await vm.SearchSaleCommand.ExecuteAsync(null);
        vm.AddIssuedLine(MakeIssuedLine(100m));
        Assert.Single(vm.IssuedLines);

        returns.Found.Clear();          // the next number matches nothing
        vm.DocumentNumberQuery = "9999";
        await vm.SearchSaleCommand.ExecuteAsync(null);

        Assert.Empty(vm.IssuedLines);
        Assert.Equal(0m, vm.IssuedTotal);
    }

    [Fact]
    public async Task ClearSearch_EmptiesTheBoxTheReceiptAndTheBasket()
    {
        var returns = new FakeReturnService(new CallLog());
        returns.Found.Add(new ExpenseListItem { Id = "doc1", DocumentNumber = "1042" });
        var vm = new ExchangeViewModel(returnService: returns, isOnline: true)
        {
            DocumentNumberQuery = "1042",
        };
        await vm.SearchSaleCommand.ExecuteAsync(null);
        vm.AddIssuedLine(MakeIssuedLine(100m));

        vm.ClearSearchCommand.Execute(null);

        Assert.Equal(string.Empty, vm.DocumentNumberQuery);
        Assert.Null(vm.SelectedSale);
        Assert.Empty(vm.IssuedLines);
    }

    [Fact]
    public async Task SubmitExchange_DiscountedReplacement_ReceiptLinesAddUpToWhatWasCharged()
    {
        // The slip prints the issued lines and then the difference, with no discount line
        // of its own — so the lines have to be the discounted ones or the customer's copy
        // does not add up.
        var rig = BuildForSubmit(returnedPrice: 50m, issuedPrice: 100m,
            quote: MakeQuote("p2", unitPrice: 100m, quantity: 1m, percent: 50m));

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Printer.ExchangeReceipt);
        Assert.Equal(0m, rig.Printer.LastDifference);     // 50 issued against 50 returned
        var issued = Assert.Single(rig.Printer.LastIssuedLines!);
        Assert.Equal(50m, issued.LineRefund);
    }

    // ---------------------------------------------------------------------------------
    // Scanning a physical item instead of hunting for its line among the returned ones.
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task ScanReturnBarcode_MatchingLine_IncrementsItsReturnQty()
    {
        var vm = new ExchangeViewModel();
        vm.SetReturnedLines(new[]
        {
            new ReturnLineVm(new ReturnDetailLine
            { Product = new ReturnProduct { Id = "pA", Barcode = "111" }, Quantity = 3, QuantityReturned = 0, AfterDiscount = 150 }),
        });
        vm.ReturnScanQuery = "111";

        await vm.ScanReturnBarcodeCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.ReturnedLines[0].ReturnQty);
        Assert.Equal(string.Empty, vm.ReturnScanQuery);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task ScanReturnBarcode_NoMatch_SetsAnError()
    {
        var vm = new ExchangeViewModel();
        vm.SetReturnedLines(new[]
        {
            new ReturnLineVm(new ReturnDetailLine
            { Product = new ReturnProduct { Id = "pA", Barcode = "111" }, Quantity = 3, QuantityReturned = 0, AfterDiscount = 150 }),
        });
        vm.ReturnScanQuery = "does-not-exist";

        await vm.ScanReturnBarcodeCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(0, vm.ReturnedLines[0].ReturnQty);
    }

    [Fact]
    public void ScanReturnBarcode_DoesNotBlockOnTheHighlightFlash_SoAFastSecondScanIsNotDropped()
    {
        // Same fix, and the same test shape, as ReturnsViewModel's own copy of this
        // test. Pins the fix itself, not just its downstream symptom. Both the buggy
        // and the fixed command eventually leave CanExecute true again — awaiting
        // ExecuteAsync to completion and THEN checking CanExecute passes either way,
        // and only a wall-clock assertion — flaky in CI — would actually tell the
        // two apart.
        //
        // Instead: fire the scan through the plain ICommand.Execute, exactly how
        // OnReturnScanKeyDown fires it, and check CanExecute the instant that call
        // returns, with no await anywhere in this test. That only reads true here
        // because the command body itself has no blocking await before its own
        // return: the fixed body hands back an already-completed Task, and awaiting
        // an already-completed task never actually suspends the awaiting method —
        // so AsyncRelayCommand's whole ExecuteAsync, including flipping IsRunning
        // back off, runs to completion synchronously before Execute returns control
        // here. Against the old body — an async method that genuinely awaited
        // Task.Delay(700) before returning — that inner await does suspend for
        // real, so Execute would return with the command still "running" and
        // CanExecute would read false here. That was the bug: a second scan
        // arriving at scanner speed found the command refusing to run, and its
        // digits sat in the now-cleared box with nothing to fire them.
        var vm = new ExchangeViewModel();
        vm.SetReturnedLines(new[]
        {
            new ReturnLineVm(new ReturnDetailLine
            { Product = new ReturnProduct { Id = "pA", Barcode = "111" }, Quantity = 3, QuantityReturned = 0, AfterDiscount = 150 }),
        });
        vm.ReturnScanQuery = "111";

        vm.ScanReturnBarcodeCommand.Execute(null);

        Assert.True(vm.ScanReturnBarcodeCommand.CanExecute(null));
        Assert.Equal(1, vm.ReturnedLines[0].ReturnQty);

        // The second scan, arriving right behind the first, must not be dropped.
        vm.ReturnScanQuery = "111";
        vm.ScanReturnBarcodeCommand.Execute(null);

        Assert.Equal(2, vm.ReturnedLines[0].ReturnQty);
    }
}
