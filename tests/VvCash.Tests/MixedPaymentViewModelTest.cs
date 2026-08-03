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

    [Fact]
    public void ExactTender_SettlesATotalThatStillCarriesSubCentFractions()
    {
        // A total is supposed to arrive already rounded to the store's money
        // scale, but the payment screen must not deadlock if one ever does not:
        // the quick-tender "exact" chip pays 621.88 against 621.884, and a
        // remainder nobody can see (or hand over) must not keep the confirm
        // button disabled with "remaining 0.00" on screen.
        var vm = new MixedPaymentViewModel(621.884m, (_, __, ___) => { });

        vm.SetQuickAmountCommand.Execute(vm.ExactAmount);

        Assert.Equal(621.88m, vm.CashAmount);
        Assert.True(vm.IsFullyPaid);
        Assert.True(vm.ConfirmPaymentCommand.CanExecute(null));
    }
}
