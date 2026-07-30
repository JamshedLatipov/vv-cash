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

    /// <summary>Unit price the server quoted this line at, or null when no server quote
    /// covers it. The server prices a cart from the warehouse catalog and ignores the
    /// unit price the register sends, so once a quote is in force this — not the cached
    /// <see cref="Models.Product.Price"/> — is what the customer is charged. Stored per
    /// unit rather than as a line total so the line still reads correctly during the
    /// debounce window after a quantity change, before the new quote lands.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineTotal))]
    [NotifyPropertyChangedFor(nameof(UnitPrice))]
    private decimal? _quotedUnitPrice;

    public Product Product { get; set; } = null!;
    public decimal UnitPrice => QuotedUnitPrice ?? Product.Price;
    public decimal LineTotal => UnitPrice * Quantity;

    /// <summary>Gross line total at the product's own pre-discount price — the
    /// strike-through figure on screen. Deliberately not quote-driven: it exists to show
    /// what the line would have cost, which is a catalog fact, not a priced one.</summary>
    public decimal LineTotalOriginal => (Product.OriginalPrice ?? Product.Price) * Quantity;

    /// <summary>Quantity without trailing zeros, so a whole-unit line still reads
    /// "2" and not "2.000" on screen and on the receipt.</summary>
    public string QuantityDisplay => Quantity == decimal.Truncate(Quantity)
        ? decimal.Truncate(Quantity).ToString(CultureInfo.InvariantCulture)
        : Quantity.ToString("0.###", CultureInfo.InvariantCulture);
}
