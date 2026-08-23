using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VvCash.ViewModels;

public partial class MixedPaymentViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemainingAmount))]
    [NotifyPropertyChangedFor(nameof(QuickInputText))]
    private decimal _totalAmount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemainingAmount))]
    [NotifyPropertyChangedFor(nameof(QuickInputText))]
    private decimal _cashAmount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemainingAmount))]
    [NotifyPropertyChangedFor(nameof(QuickInputText))]
    private decimal _cardAmount;

    // Numpad target field
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuickInputText))]
    private string _selectedMethod = "Cash";

    public decimal RemainingAmount => TotalAmount - (CashAmount + CardAmount);

    // ---- Receipt-panel helpers (redesigned payment screen) ----
    /// <summary>What has been tendered — and, by construction, exactly what the
    /// completion callback hands back. The sale document carries two money slots
    /// (paid_in_cash, paid_by_credit_card) and nothing else, so a tender this
    /// screen counts here that the callback cannot carry books the receipt as
    /// unpaid: the money is in the drawer while the document says the customer
    /// still owes it. That is what a third "gift card" tender used to do. Any new
    /// tender added here has to arrive together with a slot on the document to
    /// land in — see Payment in DocumentRequest.cs.</summary>
    public decimal PaidAmount => CashAmount + CardAmount;
    public decimal RemainingDue => Math.Max(0, RemainingAmount);
    public decimal ChangeAmount => Math.Max(0, -RemainingAmount);
    /// <summary>Under half a cent counts as settled. Totals reach this screen
    /// already rounded to the store's money scale, so normally the comparison is
    /// exact; the tolerance is there so that an amount which slips through
    /// unrounded cannot deadlock the receipt, showing "remaining 0.00" next to a
    /// confirm button that refuses to enable.</summary>
    public bool IsFullyPaid => RemainingAmount < 0.005m;
    public bool HasChange => ChangeAmount > 0;
    public double ProgressPercent => TotalAmount > 0
        ? Math.Min(100.0, (double)(PaidAmount / TotalAmount) * 100.0)
        : 100.0;

    /// <summary>What would be lent if the cashier hit "sell on credit" right now: what is
    /// still owed on this receipt. Derived the same way PosViewModel derives Remained, so
    /// the two cannot disagree about the size of the debt they are booking.</summary>
    public decimal CreditDebt => RemainingDue;

    /// <summary>Where the customer's balance lands if this sale goes on credit.</summary>
    public decimal ProjectedBalance => _currentBalance - CreditDebt;

    /// <summary>Whether the debt fits inside the ceiling. A balance is negative when the
    /// customer owes us, so the ceiling is -limit and the test is "not below it".
    ///
    /// Defers to IsFullyPaid rather than testing CreditDebt &lt;= 0 directly, and
    /// deliberately so: IsFullyPaid already carries the half-cent tolerance that keeps an
    /// unrounded total (the 621.884-against-621.88 exact-tender case elsewhere in this
    /// file) from deadlocking the confirm button — see the comment on IsFullyPaid. Testing
    /// CreditDebt &lt;= 0 here instead would reintroduce that same deadlock one screen
    /// element to the right: the confirm button enables on "remaining 0.00", but the
    /// credit button reports blocked over a residue nobody can see or pay, for a customer
    /// whose real debt is zero. Deferring to IsFullyPaid ties the two to one definition of
    /// "settled" so they cannot drift apart later.
    ///
    /// A fully tendered receipt lends nothing, so the ceiling has nothing to say about it.
    /// Confirmed design decision, not an inferred edge case — see the "zero debt" row of
    /// the Problem 4 test table in docs/superpowers/specs/2026-08-23-sync-and-storage-design.md.
    ///
    /// Nothing on the server enforces this: credit_limit is stored and serialised and
    /// never compared, in documents/ or anywhere else. If this does not stop the sale,
    /// nothing does.</summary>
    public bool IsWithinCreditLimit => IsFullyPaid || ProjectedBalance >= -_creditLimit;

    public decimal CreditLimit => _creditLimit;

    /// <summary>The customer's existing debt, sign-flipped for the cashier: positive
    /// means they owe the store. The server's own balance uses the opposite convention
    /// — negative means owed, per the comment on <see cref="IsWithinCreditLimit"/> above
    /// — so without this flip the screen would show a balance of -400 next to a limit of
    /// 500 and leave the cashier to work out unaided that -400 is being measured against
    /// -500. This is the customer's standing debt, not <see cref="CreditDebt"/> — that one
    /// is what the current receipt would add if sold on credit.
    ///
    /// Clamped to zero rather than shown negative for a customer in credit (a positive
    /// balance): a negative debt is not something a cashier should have to parse, and
    /// this screen has no job announcing store credit. Mirrors the same zero-floor
    /// <see cref="ChangeAmount"/> already applies to <see cref="RemainingAmount"/>.</summary>
    public decimal Debt => Math.Max(0, -_currentBalance);
    public bool IsCreditBlocked => HasCustomer && !IsWithinCreditLimit;

    // Quick-tender: exact remaining + round-ups (for cash payments)
    public decimal ExactAmount { get; private set; }
    public ObservableCollection<decimal> RoundUpAmounts { get; } = new();

    // Raw string buffer to support decimal points properly
    private string _currentInputBuffer = "0";

    // Quick input text representation (shown above numpad)
    public string QuickInputText => _currentInputBuffer;

    private readonly Action<bool, decimal, decimal> _onCompletion;

    /// <summary>Whether one receipt may be split across several tenders. When the
    /// store switches mixed payment off, choosing a different method moves the
    /// amount rather than adding a second tender — the cashier still picks how the
    /// customer pays, they just cannot split one receipt in two.
    ///
    /// Defaults to true so that a caller which knows nothing about feature flags
    /// keeps the behaviour this screen has always had.</summary>
    private readonly bool _allowMixed;

    /// <summary>Whether a customer was selected before Pay opened this screen.
    /// Gates <see cref="SellOnCreditCommand"/> — crediting a sale to a customer's
    /// account needs someone to charge it against.</summary>
    public bool HasCustomer { get; }

    /// <summary>Set the instant either confirmation command hands off to
    /// <see cref="_onCompletion"/>, and never cleared again: this view model is
    /// discarded once the host navigates away (success or failure), so there is
    /// nothing to resume. Exists purely to stop a second tap — of either button —
    /// from booking a second document for the same receipt while the first
    /// completion (document creation, printing) is still running.</summary>
    private bool _isSubmitting;

    /// <summary>The customer's credit ceiling and their balance, as the server reports
    /// them. One value rather than two optional parameters because the pair is only
    /// meaningful together: supplying a limit while forgetting the balance reads as "no
    /// existing debt" and silently opens the gate, and the constructor's call site is a
    /// hundred lines of lambda away from the constructor name.
    ///
    /// Null throughout means no customer, or a customer the register knows nothing
    /// about — which under the backend's COALESCE(credit_limit, 0) means credit is
    /// forbidden, not unlimited.</summary>
    public readonly record struct CreditTerms(decimal Limit, decimal Balance);

    /// <summary>Plain decimals, not the CreditTerms record itself: everything downstream
    /// (ProjectedBalance, IsWithinCreditLimit, CreditLimit and Debt below) reads these two
    /// fields and did before CreditTerms existed. See CreditTerms above for why the two
    /// travel together on the way in.</summary>
    private readonly decimal _creditLimit;
    private readonly decimal _currentBalance;

    public MixedPaymentViewModel(
        decimal totalAmount,
        Action<bool, decimal, decimal> onCompletion,
        bool allowMixed = true,
        bool hasCustomer = false,
        CreditTerms? creditTerms = null)
    {
        TotalAmount = totalAmount;
        _onCompletion = onCompletion;
        _allowMixed = allowMixed;
        HasCustomer = hasCustomer;
        _creditLimit = creditTerms?.Limit ?? 0m;
        _currentBalance = creditTerms?.Balance ?? 0m;
        RecomputeQuickAmounts();
    }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(PaidAmount));
        OnPropertyChanged(nameof(RemainingDue));
        OnPropertyChanged(nameof(ChangeAmount));
        OnPropertyChanged(nameof(IsFullyPaid));
        OnPropertyChanged(nameof(HasChange));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(CreditDebt));
        OnPropertyChanged(nameof(ProjectedBalance));
        OnPropertyChanged(nameof(IsWithinCreditLimit));
        OnPropertyChanged(nameof(IsCreditBlocked));
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
        // Not optional: the rule depends on the amounts, and this screen's amounts change
        // with every keypress. Without this line the block works only some of the time.
        SellOnCreditCommand.NotifyCanExecuteChanged();
        RecomputeQuickAmounts();
    }

    private decimal AmountOf(string method) => method switch
    {
        "Cash" => CashAmount,
        "Card" => CardAmount,
        _ => 0
    };

    private void RecomputeQuickAmounts()
    {
        // Remaining if the active method's own entry were zeroed out.
        var rem = TotalAmount - (PaidAmount - AmountOf(SelectedMethod));
        var baseAmount = rem > 0 ? rem : TotalAmount;
        ExactAmount = Math.Round(baseAmount, 2);
        OnPropertyChanged(nameof(ExactAmount));

        RoundUpAmounts.Clear();
        foreach (var step in new[] { 10m, 50m, 100m })
        {
            var up = Math.Ceiling(baseAmount / step) * step;
            if (up > baseAmount && !RoundUpAmounts.Contains(up))
                RoundUpAmounts.Add(up);
        }
    }

    partial void OnCashAmountChanged(decimal value) => NotifyDerived();
    partial void OnCardAmountChanged(decimal value) => NotifyDerived();
    partial void OnTotalAmountChanged(decimal value) => NotifyDerived();

    [RelayCommand]
    private void Close()
    {
        _onCompletion(false, 0, 0);
    }

    [RelayCommand]
    private void Back()
    {
        _onCompletion(false, 0, 0);
    }

    private bool CanConfirmPayment() => IsFullyPaid && !_isSubmitting;

    [RelayCommand(CanExecute = nameof(CanConfirmPayment))]
    private void ConfirmPayment()
    {
        if (!IsFullyPaid || _isSubmitting) return;
        Submit();
    }

    private bool CanSellOnCredit() => HasCustomer && !_isSubmitting && IsWithinCreditLimit;

    /// <summary>Books the sale with whatever has been tendered so far and lets
    /// the rest ride as the selected customer's debt — PosViewModel derives the
    /// debt amount the same way it derives change, from TotalAmount minus what
    /// this hands back.</summary>
    [RelayCommand(CanExecute = nameof(CanSellOnCredit))]
    private void SellOnCredit()
    {
        if (!HasCustomer || _isSubmitting || !IsWithinCreditLimit) return;
        Submit();
    }

    private void Submit()
    {
        _isSubmitting = true;
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
        SellOnCreditCommand.NotifyCanExecuteChanged();
        _onCompletion(true, CashAmount, CardAmount);
    }

    partial void OnSelectedMethodChanged(string value)
    {
        if (!_allowMixed)
        {
            // Single-tender mode: the newly selected method keeps whatever amount it
            // already carries (normally zero), and every other tender is zeroed so the
            // amount follows the cashier's choice instead of accumulating across
            // methods. This runs here rather than in the SelectMethod command because
            // SelectedMethod can also be assigned directly (e.g. from tests or other
            // callers), and this hook fires on every path that changes it.
            if (value != "Cash") CashAmount = 0;
            if (value != "Card") CardAmount = 0;
        }

        _currentInputBuffer = value switch
        {
            "Cash" => CashAmount.ToString("0.##", CultureInfo.InvariantCulture),
            "Card" => CardAmount.ToString("0.##", CultureInfo.InvariantCulture),
            _ => "0"
        };
        RecomputeQuickAmounts();
    }

    [RelayCommand]
    private void SelectMethod(string method)
    {
        SelectedMethod = method;
    }

    [RelayCommand]
    private void SetQuickAmount(decimal amount)
    {
        _currentInputBuffer = amount.ToString("0.##", CultureInfo.InvariantCulture);
        UpdateAmount(amount);
        OnPropertyChanged(nameof(QuickInputText));
    }

    [RelayCommand]
    private void AddDigit(string digit)
    {
        if (digit == "." && _currentInputBuffer.Contains("."))
        {
            return;
        }

        if (_currentInputBuffer == "0" && digit != ".")
        {
            _currentInputBuffer = digit;
        }
        else
        {
            _currentInputBuffer += digit;
        }

        if (decimal.TryParse(_currentInputBuffer, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal newValue))
        {
            UpdateAmount(newValue);
        }

        OnPropertyChanged(nameof(QuickInputText));
    }

    [RelayCommand]
    private void Backspace()
    {
        if (_currentInputBuffer.Length > 0)
        {
            _currentInputBuffer = _currentInputBuffer.Substring(0, _currentInputBuffer.Length - 1);
            if (string.IsNullOrEmpty(_currentInputBuffer))
            {
                _currentInputBuffer = "0";
            }

            if (decimal.TryParse(_currentInputBuffer, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal newValue))
            {
                UpdateAmount(newValue);
            }

            OnPropertyChanged(nameof(QuickInputText));
        }
    }

    [RelayCommand]
    private void Reset()
    {
        _currentInputBuffer = "0";
        UpdateAmount(0);
        OnPropertyChanged(nameof(QuickInputText));
    }

    [RelayCommand]
    private void AllAtOnce()
    {
        // Add remaining amount to the selected method
        if (RemainingAmount > 0)
        {
            decimal newAmount = RemainingAmount + AmountOf(SelectedMethod);
            _currentInputBuffer = newAmount.ToString("0.##", CultureInfo.InvariantCulture);
            UpdateAmount(newAmount);
            OnPropertyChanged(nameof(QuickInputText));
        }
    }

    private void UpdateAmount(decimal newValue)
    {
        switch (SelectedMethod)
        {
            case "Cash":
                CashAmount = newValue;
                break;
            case "Card":
                CardAmount = newValue;
                break;
        }
    }
}
