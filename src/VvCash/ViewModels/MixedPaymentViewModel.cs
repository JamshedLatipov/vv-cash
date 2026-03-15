using System;
using System.Globalization;
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

    // Raw string buffer to support decimal points properly
    private string _currentInputBuffer = "0";

    // Quick input text representation (shown above numpad)
    public string QuickInputText => _currentInputBuffer;

    private readonly Avalonia.Controls.Window _window;

    public MixedPaymentViewModel(Avalonia.Controls.Window window, decimal totalAmount)
    {
        TotalAmount = totalAmount;
        _window = window;
    }

    [RelayCommand]

    private void Close()
    {
        _window.Close(false);
    }

    [RelayCommand]
    private void Back()
    {
        _window.Close(false);
    }

    [RelayCommand]
    private void ConfirmPayment()
    {
        _window.Close(true);
    }

    partial void OnSelectedMethodChanged(string value)
    {
        _currentInputBuffer = value switch
        {
            "Cash" => CashAmount.ToString("0.##", CultureInfo.InvariantCulture),
            "Card" => CardAmount.ToString("0.##", CultureInfo.InvariantCulture),
            "Gift" => GiftAmount.ToString("0.##", CultureInfo.InvariantCulture),
            _ => "0"
        };
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
            decimal newAmount = RemainingAmount + (SelectedMethod switch
            {
                "Cash" => CashAmount,
                "Card" => CardAmount,
                "Gift" => GiftAmount,
                _ => 0
            });
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
