using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

public class MixedPaymentViewModelTest
{
    [Fact]
    public void MixedAllowed_SplittingAcrossTwoTendersStillWorks()
    {
        // Default behaviour (allowMixed defaults to true) must keep splitting a
        // receipt across several tenders — this guards against a regression.
        var vm = new MixedPaymentViewModel(100m, (_, __, ___) => { });

        vm.CashAmount = 30m;
        vm.SelectMethodCommand.Execute("Card");
        vm.CardAmount = 70m;

        Assert.Equal(30m, vm.CashAmount);
        Assert.Equal(70m, vm.CardAmount);
        Assert.True(vm.IsFullyPaid);
        Assert.Equal(0m, vm.RemainingAmount);
    }

    [Fact]
    public void MixedDisabled_SwitchingMethodMovesTheAmountInsteadOfAddingToIt()
    {
        var vm = new MixedPaymentViewModel(100m, (_, __, ___) => { }, allowMixed: false);

        vm.CashAmount = 100m;
        vm.SelectMethodCommand.Execute("Card");
        vm.CardAmount = 100m;

        Assert.Equal(0m, vm.CashAmount);
        Assert.Equal(100m, vm.CardAmount);
        Assert.True(vm.IsFullyPaid);
        Assert.Equal(0m, vm.RemainingAmount);
    }

    [Fact]
    public void MixedDisabled_SwitchingMethodClearsTheOtherTenderImmediately()
    {
        var vm = new MixedPaymentViewModel(100m, (_, __, ___) => { }, allowMixed: false);

        vm.CashAmount = 40m;
        vm.SelectMethodCommand.Execute("Card");

        // The other tender must already be zero before any new digit is typed
        // under the newly selected method.
        Assert.Equal(0m, vm.CashAmount);
    }

    [Fact]
    public void MixedDisabled_SingleTenderPaymentIsLeftUntouched()
    {
        var vm = new MixedPaymentViewModel(100m, (_, __, ___) => { }, allowMixed: false);

        vm.CashAmount = 100m;

        Assert.Equal(100m, vm.CashAmount);
        Assert.True(vm.IsFullyPaid);
        Assert.Equal(0m, vm.RemainingAmount);
        Assert.True(vm.ConfirmPaymentCommand.CanExecute(null));
    }
}
