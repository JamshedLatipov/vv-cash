using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Models;

namespace VvCash.ViewModels;

public partial class TransactionSuccessViewModel : ViewModelBase
{
    private readonly Action _onNewSale;

    [ObservableProperty] private string _transactionId;
    [ObservableProperty] private string _dateString;
    [ObservableProperty] private ObservableCollection<CartItem> _items = new();

    [ObservableProperty] private decimal _subtotal;
    [ObservableProperty] private decimal _tax;
    [ObservableProperty] private decimal _discount;
    [ObservableProperty] private decimal _total;

    // Mocked for the design
    [ObservableProperty] private string _paymentMethod = "Visa Direct • • • • 4412";
    [ObservableProperty] private string _authCode = "994210";
    [ObservableProperty] private string _clientName = "Иван Иванов (+7 999 *** ** 11)";
    [ObservableProperty] private string _pointsEarned = "+124 Б";
    [ObservableProperty] private string _pointsBalance = "2 450 Б";

    public TransactionSuccessViewModel(
        string transactionId,
        ObservableCollection<CartItem> items,
        decimal subtotal,
        decimal tax,
        decimal discount,
        decimal total,
        Action onNewSale)
    {
        _transactionId = transactionId;
        _items = items;
        _subtotal = subtotal;
        _tax = tax;
        _discount = discount;
        _total = total;
        _onNewSale = onNewSale;

        _dateString = DateTime.Now.ToString("dd.MM.yyyy, HH:mm");
    }

    [RelayCommand]
    private void PrintReceipt() { }

    [RelayCommand]
    private void EmailReceipt() { }

    [RelayCommand]
    private void SmsReceipt() { }

    [RelayCommand]
    private void NewSale()
    {
        _onNewSale?.Invoke();
    }
}
