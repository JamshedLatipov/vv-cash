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
    // Records what was actually sent, and hands back a canned outcome. The default
    // is the bleakest one — nothing answered at all — so a test that cares about
    // success has to say so.
    private sealed class FakeExchangeService : IExchangeService
    {
        public ExchangeOutcome Outcome = new();
        public readonly List<ExchangeRequest> Requests = new();
        public Task<ExchangeOutcome> CreateExchangeAsync(string expenseDocumentId, ExchangeRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(Outcome);
        }

        public static ExchangeOutcome Booked(string documentNumber, decimal difference) => new()
        {
            StatusCode = 200,
            Body = new ExchangeResponseBody { ExpenseDocumentNumber = documentNumber, Difference = difference },
        };
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
        public event System.EventHandler<PrinterStatus>? StatusChanged;
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

    private static ExchangeViewModel BuildForSubmit(
        FakeExchangeService exchange, CountingPrinter printer, ICashFeatureService features,
        decimal returnedPrice = 80m, decimal issuedPrice = 100m)
    {
        var vm = new ExchangeViewModel(exchangeService: exchange, printerService: printer, features: features, isOnline: true);
        vm.SelectedSale = new ExpenseListItem { Id = "doc1", DocumentNumber = "9" };
        vm.SetReturnedLines(new[] { MakeReturnedLine(returnedPrice) });
        vm.AddIssuedLine(MakeIssuedLine(issuedPrice));
        return vm;
    }

    [Fact]
    public async Task SubmitExchange_Success_WithDifference_PrintsOnceWithServerDocumentNumber_AndOpensDrawer()
    {
        var exchange = new FakeExchangeService { Outcome = FakeExchangeService.Booked("77", 20m) };
        var printer = new CountingPrinter();
        var vm = BuildForSubmit(exchange, printer, new FakeCashFeatureService()); // returned 80, issued 100

        await vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Equal(1, printer.ExchangeReceipt);
        Assert.Equal("77", printer.LastDocumentNumber); // from the server response, not invented locally
        Assert.Equal(20m, printer.LastDifference);
        Assert.Equal(1, printer.Drawer); // money actually moved
    }

    [Fact]
    public async Task SubmitExchange_ServerRefuses_NoPrint_NoDrawer_BasketsUntouched()
    {
        var exchange = new FakeExchangeService
        {
            Outcome = new ExchangeOutcome { StatusCode = 400, Message = "declared total 100.00 does not match the calculated total 80.00" }
        };
        var printer = new CountingPrinter();
        var vm = BuildForSubmit(exchange, printer, new FakeCashFeatureService());

        await vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Equal(0, printer.ExchangeReceipt);
        Assert.Equal(0, printer.Drawer);
        Assert.Single(vm.IssuedLines); // left exactly as the cashier built it, so they can retry
        Assert.Equal(1, vm.ReturnedLines.Single().ReturnQty);
    }

    [Fact]
    public async Task SubmitExchange_RetryOfTheSameBaskets_SendsTheSameDocumentHash()
    {
        // The dangerous case is a first attempt that commits server-side while its
        // reply is lost, so the cashier presses submit again. A hash minted per press
        // makes that second press a brand new exchange — a second return plus a
        // second sale for the same goods; the same hash gets 409 from the server
        // instead. The refusal below stands in for that lost reply: it leaves both
        // baskets exactly as they were, which is the state a retry happens from.
        var exchange = new FakeExchangeService(); // nothing answered: the lost reply
        var vm = BuildForSubmit(exchange, new CountingPrinter(), new FakeCashFeatureService());

        await vm.SubmitExchangeCommand.ExecuteAsync(null);
        await vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Equal(2, exchange.Requests.Count);
        Assert.NotEmpty(exchange.Requests[0].DocumentHash);
        Assert.Equal(exchange.Requests[0].DocumentHash, exchange.Requests[1].DocumentHash);

        // A changed basket is a different exchange and must not inherit the key —
        // otherwise the server would refuse it as a duplicate of the one above.
        vm.AddIssuedLine(MakeIssuedLine(30m));
        await vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Equal(3, exchange.Requests.Count);
        Assert.NotEqual(exchange.Requests[1].DocumentHash, exchange.Requests[2].DocumentHash);
    }

    [Fact]
    public async Task SubmitExchange_ExactPriceMatch_PrintsReceipt_ButDoesNotOpenDrawer()
    {
        var exchange = new FakeExchangeService { Outcome = FakeExchangeService.Booked("5", 0m) };
        var printer = new CountingPrinter();
        var vm = BuildForSubmit(exchange, printer, new FakeCashFeatureService(), returnedPrice: 80m, issuedPrice: 80m);

        await vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Equal(1, printer.ExchangeReceipt);
        Assert.Equal(0, printer.Drawer); // nothing for the drawer to hand over or collect
    }

    [Fact]
    public async Task SubmitExchange_RefusedWithAReason_ShowsThatReason_NotAGenericFailure()
    {
        // "Срок обмена истёк" and "этот обмен уже проведён" call for opposite
        // reactions from the cashier, and neither is a connection problem — a screen
        // that collapses all three into one message hides the only information that
        // tells them what to do next.
        const string reason = "exchange period of 14 days has expired for this sale";
        var exchange = new FakeExchangeService { Outcome = new ExchangeOutcome { StatusCode = 400, Message = reason } };
        var vm = BuildForSubmit(exchange, new CountingPrinter(), new FakeCashFeatureService());

        await vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Equal(reason, vm.ErrorMessage);
    }

    [Fact]
    public async Task SubmitExchange_NothingAnswered_ReportsNoConnection()
    {
        // The other side of the test above: no status at all is the one case where
        // the request may never have reached the server.
        var exchange = new FakeExchangeService(); // no body, no status, no message
        var vm = BuildForSubmit(exchange, new CountingPrinter(), new FakeCashFeatureService());

        await vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.Equal(I18nService.Instance["NoConnection"], vm.ErrorMessage);
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
        // server's return leg spreads the same figure over the sold quantity
        // (refundPerUnit), so reading it as per-unit shows the cashier a refund
        // where the server computes money owed, and difference_payment goes out wrong.
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
    /// money. The exchange endpoint compares the result against the declared
    /// payment.to_pay and answers 400 when the two disagree.</summary>
    private static decimal ServerCalculatedTotal(DocumentRequest doc) =>
        doc.Products.Sum(p =>
        {
            var lineTotal = p.SellPrice * p.Quantity;
            return doc.Payment.DiscountType == "percent"
                ? lineTotal - lineTotal * p.DiscountPercent / 100m
                : lineTotal;
        }) - (doc.Payment.DiscountType == "cash" ? doc.Payment.Discount : 0m);

    [Fact]
    public void BuildRequest_IssuedProductWithCatalogDiscount_DeclaredTotalSurvivesTheServersOwnRecalculation()
    {
        // Product.Price is the already-discounted price the screen prices the line at,
        // so the request must not also ask for the catalog percent to be taken off
        // again — the server's total would land below the declared to_pay and the
        // exchange endpoint refuses the whole exchange over that gap.
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

        var req = vm.BuildRequest();

        Assert.Equal(160m, vm.IssuedTotal);                      // what the cashier is shown
        Assert.Equal(vm.IssuedTotal, req.Issued.Payment.ToPay);  // and what is declared
        Assert.Equal(req.Issued.Payment.ToPay, ServerCalculatedTotal(req.Issued));
    }

    [Fact]
    public void CanSubmit_RequiresOnline_Allowed_AndBothBasketsFilled()
    {
        var vm = new ExchangeViewModel();
        vm.SetReturnedLines(new[] { MakeReturnedLine(80m) });
        vm.AddIssuedLine(MakeIssuedLine(100m));

        vm.IsOnline = false;
        vm.ExchangeAllowed = true;
        Assert.False(vm.CanSubmit); // offline: an exchange cannot be queued

        vm.IsOnline = true;
        vm.ExchangeAllowed = false;
        Assert.False(vm.CanSubmit); // exchange window on this receipt has expired

        vm.ExchangeAllowed = true;
        Assert.True(vm.CanSubmit); // baseline: online, allowed, both baskets non-empty

        vm.SetReturnedLines(System.Array.Empty<ReturnLineVm>());
        Assert.False(vm.CanSubmit); // nothing selected to return

        vm.SetReturnedLines(new[] { MakeReturnedLine(80m) });
        vm.RemoveIssuedLine(vm.IssuedLines.Single());
        Assert.False(vm.CanSubmit); // nothing selected to issue
    }
}
