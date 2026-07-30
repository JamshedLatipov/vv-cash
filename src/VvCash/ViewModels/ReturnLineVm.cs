using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Models.Api;

namespace VvCash.ViewModels;

public partial class ReturnLineVm : ObservableObject
{
    public string ProductId { get; }
    public string Name { get; }
    public string? Barcode { get; }
    public int SoldQty { get; }
    public int AlreadyReturned { get; }
    public int MaxReturnable { get; }

    /// <summary>Discounted price of a single unit. The API's <c>after_discount</c>
    /// is the discounted total of the whole sold line, so it has to be spread over
    /// the sold quantity — exactly how the server refunds it (refundPerUnit). Read
    /// as a unit price it would multiply a line sold in twos or threes by that
    /// quantity again.</summary>
    public decimal UnitPrice { get; }
    public bool IsReturnable => MaxReturnable > 0;

    public event Action? RefundChanged;

    private int _returnQty;
    public int ReturnQty
    {
        get => _returnQty;
        set
        {
            var clamped = value < 0 ? 0 : (value > MaxReturnable ? MaxReturnable : value);
            if (SetProperty(ref _returnQty, clamped))
            {
                OnPropertyChanged(nameof(LineRefund));
                RefundChanged?.Invoke();
            }
        }
    }

    public decimal LineRefund => ReturnQty * UnitPrice;

    public ReturnLineVm(ReturnDetailLine line)
    {
        ProductId = line.Product?.Id ?? string.Empty;
        Name = line.Product?.Name ?? string.Empty;
        Barcode = line.Product?.Barcode;
        SoldQty = line.Quantity;
        AlreadyReturned = line.QuantityReturned;
        MaxReturnable = Math.Max(0, line.Quantity - line.QuantityReturned);
        // A non-positive sold quantity is malformed data; price it at zero rather
        // than dividing by it.
        UnitPrice = line.Quantity > 0 ? line.AfterDiscount / line.Quantity : 0m;
    }

    [RelayCommand]
    private void Increment() => ReturnQty += 1;

    [RelayCommand]
    private void Decrement() => ReturnQty -= 1;
}
