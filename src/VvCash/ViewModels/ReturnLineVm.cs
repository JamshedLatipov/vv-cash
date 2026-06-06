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
        UnitPrice = line.AfterDiscount;
    }

    [RelayCommand]
    private void Increment() => ReturnQty += 1;

    [RelayCommand]
    private void Decrement() => ReturnQty -= 1;
}
