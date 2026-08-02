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
    public string? Article { get; }
    public int SoldQty { get; }
    public int AlreadyReturned { get; }
    public int MaxReturnable { get; }

    /// <summary>Catalog price of one unit at the moment of the sale, before any
    /// discount — the API's <c>sold_price</c>. Shown next to what was actually paid so
    /// the cashier can see the two differ.</summary>
    public decimal SoldPrice { get; }

    /// <summary>What the customer paid for the whole line — the API's
    /// <c>after_discount</c>, i.e. the sold quantity, not the returned one.</summary>
    public decimal PaidTotal { get; }

    /// <summary>What came off the line, derived from the two figures printed beside it
    /// (catalog × sold quantity, less what was paid) rather than from
    /// <c>discount_in_unit</c>, so the three numbers on the card always add up.
    /// Zero on legacy rows that carry no <c>sold_price</c> — see the constructor.</summary>
    public decimal LineDiscount { get; }

    /// <summary>The percent the sale recorded, straight from the API. Not re-derived from
    /// <see cref="LineDiscount"/>: a document-level discount is spread over the lines in
    /// money, so the arithmetic percent and the recorded one need not agree, and the
    /// recorded one is what the back office sees.</summary>
    public decimal DiscountPercent { get; }

    public bool HasArticle => !string.IsNullOrWhiteSpace(Article);
    public bool HasBarcode => !string.IsNullOrWhiteSpace(Barcode);
    public bool HasDiscount => LineDiscount > 0m;

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
        Article = line.Product?.Article;
        SoldQty = line.Quantity;
        AlreadyReturned = line.QuantityReturned;
        MaxReturnable = Math.Max(0, line.Quantity - line.QuantityReturned);
        // A non-positive sold quantity is malformed data; price it at zero rather
        // than dividing by it.
        UnitPrice = line.Quantity > 0 ? line.AfterDiscount / line.Quantity : 0m;
        SoldPrice = line.SoldPrice;
        PaidTotal = line.AfterDiscount;
        DiscountPercent = line.DiscountInPercent;
        // Rows imported before after_discount existed, and Excel orders, can carry a zero
        // sold_price with a real after_discount. Subtracting from zero there would print a
        // negative discount the size of the whole line, so treat "no catalog price" as
        // "nothing to compare against" and show no discount at all.
        var beforeDiscount = SoldPrice * line.Quantity;
        LineDiscount = beforeDiscount > 0m ? Math.Max(0m, beforeDiscount - line.AfterDiscount) : 0m;
    }

    [RelayCommand]
    private void Increment() => ReturnQty += 1;

    [RelayCommand]
    private void Decrement() => ReturnQty -= 1;
}
