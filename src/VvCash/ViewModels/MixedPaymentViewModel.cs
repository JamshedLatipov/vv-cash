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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemainingAmount))]
    [NotifyPropertyChangedFor(nameof(QuickInputText))]
    private decimal _giftAmount;

    // Numpad target field
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuickInputText))]
    private string _selectedMethod = "Cash";

    public decimal RemainingAmount => TotalAmount - (CashAmount + CardAmount + GiftAmount);

    // ---- Receipt-panel helpers (redesigned payment screen) ----
    public decimal PaidAmount => CashAmount + CardAmount + GiftAmount;
    public decimal RemainingDue => Math.Max(0, RemainingAmount);
    public decimal ChangeAmount => Math.Max(0, -RemainingAmount);
    public bool IsFullyPaid => RemainingAmount <= 0;
    public bool HasChange => ChangeAmount > 0;
    public double ProgressPercent => TotalAmount > 0
        ? Math.Min(100.0, (double)(PaidAmount / TotalAmount) * 100.0)
        : 100.0;

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

    public MixedPaymentViewModel(decimal totalAmount, Action<bool, decimal, decimal> onCompletion, bool allowMixed = true)
    {
        TotalAmount = totalAmount;
        _onCompletion = onCompletion;
        _allowMixed = allowMixed;
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
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
        RecomputeQuickAmounts();
    }

    private decimal AmountOf(string method) => method switch
    {
        "Cash" => CashAmount,
        "Card" => CardAmount,
        "Gift" => GiftAmount,
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
    partial void OnGiftAmountChanged(decimal value) => NotifyDerived();
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

    private bool CanConfirmPayment() => IsFullyPaid;

    [RelayCommand(CanExecute = nameof(CanConfirmPayment))]
    private void ConfirmPayment()
    {
        if (!IsFullyPaid) return;
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
            if (value != "Gift") GiftAmount = 0;
        }

        _currentInputBuffer = value switch
        {
            "Cash" => CashAmount.ToString("0.##", CultureInfo.InvariantCulture),
            "Card" => CardAmount.ToString("0.##", CultureInfo.InvariantCulture),
            "Gift" => GiftAmount.ToString("0.##", CultureInfo.InvariantCulture),
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
            case "Gift":
                GiftAmount = newValue;
                break;
        }
    }
}
