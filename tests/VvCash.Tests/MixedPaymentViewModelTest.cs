using System.Collections.Generic;
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

    [Fact]
    public void ConfirmPayment_CalledTwiceInARow_OnlyCompletesOnce()
    {
        // Nothing on this screen disables the confirm button while the first
        // tap's completion callback is still running (it books a document and
        // prints a receipt) — a second tap before that finishes must not book
        // a second document for the same receipt.
        var completions = 0;
        var vm = new MixedPaymentViewModel(100m, (_, __, ___) => { completions++; });
        vm.CashAmount = 100m;

        vm.ConfirmPaymentCommand.Execute(null);
        vm.ConfirmPaymentCommand.Execute(null);

        Assert.Equal(1, completions);
    }

    [Fact]
    public void ConfirmPayment_AfterConfirming_CommandCanNoLongerExecute()
    {
        var vm = new MixedPaymentViewModel(100m, (_, __, ___) => { });
        vm.CashAmount = 100m;

        vm.ConfirmPaymentCommand.Execute(null);

        Assert.False(vm.ConfirmPaymentCommand.CanExecute(null));
    }

    [Fact]
    public void NoCustomerSelected_SellOnCreditIsNotAllowed()
    {
        var vm = new MixedPaymentViewModel(100m, (_, __, ___) => { }, hasCustomer: false);
        vm.CashAmount = 40m;

        Assert.False(vm.SellOnCreditCommand.CanExecute(null));
    }

    [Fact]
    public void CustomerSelected_SellOnCreditConfirmsAPartialPayment()
    {
        // The remainder becomes the customer's debt — PosViewModel computes it
        // from TotalAmount minus what SellOnCredit hands back here, same as a
        // normal ConfirmPayment. Credit limit is not what this test is about, so it
        // passes one with plenty of headroom for the 60 that goes on credit here.
        var completions = new List<(bool result, decimal cash, decimal card)>();
        var vm = new MixedPaymentViewModel(100m, (result, cash, card) => completions.Add((result, cash, card)), hasCustomer: true, creditLimit: 1000m);
        vm.CashAmount = 40m;

        Assert.False(vm.IsFullyPaid);
        Assert.True(vm.SellOnCreditCommand.CanExecute(null));

        vm.SellOnCreditCommand.Execute(null);

        Assert.Single(completions);
        Assert.Equal((true, 40m, 0m), completions[0]);
    }

    [Fact]
    public void ATenderTheDocumentCannotCarry_CannotSettleTheReceipt()
    {
        // The sale document has exactly two money slots — paid_in_cash and
        // paid_by_credit_card — and the completion callback carries exactly those
        // two. A third tender that this screen counted toward IsFullyPaid but had
        // nowhere to hand back booked the receipt as unpaid: the money was in the
        // drawer and the document said the customer still owed all of it.
        var vm = new MixedPaymentViewModel(100m, (_, __, ___) => { });

        vm.SelectMethodCommand.Execute("Gift");
        vm.SetQuickAmountCommand.Execute(100m);

        Assert.False(vm.IsFullyPaid);
        Assert.False(vm.ConfirmPaymentCommand.CanExecute(null));
    }

    [Fact]
    public void ConfirmPayment_HandsBackEveryTenderItCountedAsPaid()
    {
        // The invariant the bug above broke: whatever PaidAmount counts toward
        // settling the receipt must be exactly what reaches the document.
        var completions = new List<(decimal cash, decimal card)>();
        var vm = new MixedPaymentViewModel(100m, (_, cash, card) => completions.Add((cash, card)));

        vm.CashAmount = 60m;
        vm.SelectMethodCommand.Execute("Card");
        vm.CardAmount = 40m;

        vm.ConfirmPaymentCommand.Execute(null);

        Assert.Equal(vm.PaidAmount, completions[0].cash + completions[0].card);
    }

    [Fact]
    public void SellOnCredit_CalledTwiceInARow_OnlyCompletesOnce()
    {
        // Credit limit is not what this test is about, so it passes one with plenty
        // of headroom for the 60 that goes on credit here.
        var completions = 0;
        var vm = new MixedPaymentViewModel(100m, (_, __, ___) => { completions++; }, hasCustomer: true, creditLimit: 1000m);
        vm.CashAmount = 40m;

        vm.SellOnCreditCommand.Execute(null);
        vm.SellOnCreditCommand.Execute(null);

        Assert.Equal(1, completions);
    }

    private static MixedPaymentViewModel Credit(decimal total, decimal? limit, decimal? balance)
        => new(total, (_, _, _) => { }, allowMixed: true, hasCustomer: true,
               creditLimit: limit, currentBalance: balance);

    [Fact]
    public void SellOnCredit_ExactlyAtTheLimit_IsAllowed()
    {
        // Owes 400 already, limit 500, this sale adds 100 -> lands exactly on -500.
        var vm = Credit(100m, limit: 500m, balance: -400m);
        Assert.True(vm.SellOnCreditCommand.CanExecute(null));
    }

    [Fact]
    public void SellOnCredit_OneCentOverTheLimit_IsBlocked()
    {
        var vm = Credit(100.01m, limit: 500m, balance: -400m);
        Assert.False(vm.SellOnCreditCommand.CanExecute(null));
    }

    /// <summary>A null limit arrives as COALESCE(credit_limit, 0) does on the wire, and
    /// zero means credit is not allowed for this customer — not that it is unlimited.</summary>
    [Fact]
    public void SellOnCredit_NoLimitSet_BlocksAnyDebt()
    {
        var vm = Credit(1m, limit: null, balance: 0m);
        Assert.False(vm.SellOnCreditCommand.CanExecute(null));
    }

    /// <summary>Nothing is being lent, so the limit has nothing to say. Guards against
    /// deriving the debt from TotalAmount instead of from what is still owed.</summary>
    [Fact]
    public void SellOnCredit_FullyTendered_IsAllowedRegardlessOfLimit()
    {
        var vm = Credit(100m, limit: 0m, balance: -9999m);
        vm.CashAmount = 100m;
        Assert.True(vm.SellOnCreditCommand.CanExecute(null));
    }

    /// <summary>The button has to re-evaluate as the cashier types. Without
    /// NotifyCanExecuteChanged in NotifyDerived the rule is computed once, on a screen
    /// whose amounts change constantly, and the block works only some of the time.</summary>
    [Fact]
    public void SellOnCredit_ReevaluatesAsAmountsChange()
    {
        var vm = Credit(200m, limit: 100m, balance: 0m);
        Assert.False(vm.SellOnCreditCommand.CanExecute(null));

        vm.CashAmount = 150m;   // debt drops to 50, inside the limit

        Assert.True(vm.SellOnCreditCommand.CanExecute(null));
    }
}
