using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VvCash.Models;

public partial class CartItem : ObservableObject
{
    /// <summary>Decimal, not int: weighted goods sell in kilos, and the sale
    /// document has always accepted a fractional quantity — truncating here would
    /// bill 1.4 kg as 1 kg.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineTotal))]
    [NotifyPropertyChangedFor(nameof(QuantityDisplay))]
    private decimal _quantity;

    public Product Product { get; set; } = null!;
    public decimal LineTotal => Product.Price * Quantity;
    public decimal LineTotalOriginal => (Product.OriginalPrice ?? Product.Price) * Quantity;

    /// <summary>Quantity without trailing zeros, so a whole-unit line still reads
    /// "2" and not "2.000" on screen and on the receipt.</summary>
    public string QuantityDisplay => Quantity == decimal.Truncate(Quantity)
        ? decimal.Truncate(Quantity).ToString(CultureInfo.InvariantCulture)
        : Quantity.ToString("0.###", CultureInfo.InvariantCulture);
}
