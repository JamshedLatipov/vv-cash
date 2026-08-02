using VvCash.Models.Api;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

public class ReturnLineVmTest
{
    private static ReturnLineVm Make(int qty, int returned, decimal after) =>
        new(new ReturnDetailLine
        {
            Product = new ReturnProduct { Id = "p", Name = "Salad" },
            Quantity = qty, QuantityReturned = returned, AfterDiscount = after
        });

    [Fact]
    public void MaxReturnable_IsSoldMinusReturned()
    {
        var vm = Make(3, 1, 50m);
        Assert.Equal(2, vm.MaxReturnable);
        Assert.True(vm.IsReturnable);
    }

    [Fact]
    public void ReturnQty_ClampsToRange()
    {
        var vm = Make(3, 1, 50m); // max 2
        vm.ReturnQty = 5;
        Assert.Equal(2, vm.ReturnQty);
        vm.ReturnQty = -4;
        Assert.Equal(0, vm.ReturnQty);
    }

    [Fact]
    public void LineRefund_IsQtyTimesUnitPrice()
    {
        // 150 is the line's after_discount total for the 3 units sold, i.e. 50 a unit.
        var vm = Make(3, 0, 150m);
        vm.ReturnQty = 2;
        Assert.Equal(50m, vm.UnitPrice);
        Assert.Equal(100m, vm.LineRefund);
    }

    [Fact]
    public void FullyReturned_NotReturnable()
    {
        var vm = Make(1, 1, 50m);
        Assert.Equal(0, vm.MaxReturnable);
        Assert.False(vm.IsReturnable);
        vm.ReturnQty = 1;
        Assert.Equal(0, vm.ReturnQty);
    }

    [Fact]
    public void IdentifiersComeFromTheProduct()
    {
        var vm = new ReturnLineVm(new ReturnDetailLine
        {
            Product = new ReturnProduct { Id = "p", Name = "Salad", Article = "A-17", Barcode = "4780000000001" },
            Quantity = 1, AfterDiscount = 50m,
        });
        Assert.Equal("A-17", vm.Article);
        Assert.Equal("4780000000001", vm.Barcode);
        Assert.True(vm.HasArticle);
        Assert.True(vm.HasBarcode);
    }

    [Fact]
    public void MissingIdentifiersAreNotShown()
    {
        var vm = Make(1, 0, 50m);
        Assert.False(vm.HasArticle);
        Assert.False(vm.HasBarcode);
    }

    [Fact]
    public void PaidTotal_IsWhatTheLineActuallyCost()
    {
        // Sold 3 at a catalog 60, paid 150 for the line — i.e. 30 came off.
        var vm = new ReturnLineVm(new ReturnDetailLine
        {
            Product = new ReturnProduct { Id = "p", Name = "Salad" },
            Quantity = 3, SoldPrice = 60m, AfterDiscount = 150m, DiscountInPercent = 16.67m,
        });
        Assert.Equal(150m, vm.PaidTotal);
        Assert.Equal(60m, vm.SoldPrice);
        Assert.Equal(30m, vm.LineDiscount);
        Assert.Equal(16.67m, vm.DiscountPercent);
        Assert.True(vm.HasDiscount);
    }

    [Fact]
    public void UndiscountedLine_ShowsNoDiscount()
    {
        var vm = new ReturnLineVm(new ReturnDetailLine
        {
            Product = new ReturnProduct { Id = "p", Name = "Salad" },
            Quantity = 2, SoldPrice = 25m, AfterDiscount = 50m,
        });
        Assert.Equal(0m, vm.LineDiscount);
        Assert.False(vm.HasDiscount);
    }

    [Fact]
    public void LegacyLineWithoutSoldPrice_ShowsNoDiscount()
    {
        // Older rows carry sold_price 0 with after_discount still filled; deriving the
        // discount from them would print a negative "discount" the size of the line.
        var vm = new ReturnLineVm(new ReturnDetailLine
        {
            Product = new ReturnProduct { Id = "p", Name = "Salad" },
            Quantity = 2, SoldPrice = 0m, AfterDiscount = 50m,
        });
        Assert.Equal(0m, vm.LineDiscount);
        Assert.False(vm.HasDiscount);
    }
}
