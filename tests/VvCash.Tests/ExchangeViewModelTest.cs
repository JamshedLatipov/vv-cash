using System.Linq;
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

public class ExchangeViewModelTest
{
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
