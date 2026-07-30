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
}
