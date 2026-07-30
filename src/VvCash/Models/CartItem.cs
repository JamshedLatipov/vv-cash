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

    /// <summary>Which unit the cashier typed this line in. Drives the quantity
    /// pad and how the line reads on screen and on the receipt; it never
    /// affects money, which is always pieces × unit price.</summary>
    [ObservableProperty]
    private bool _enteredInUnit;

    /// <summary>The line's amount in the product's secondary unit.
    ///
    /// Stored rather than derived from Quantity × factor, because for a
    /// divisible product the two differ: 12.5 m² becomes 52.083333 pieces,
    /// which multiplies back to 12.49999992. The server accepts either inside
    /// its tolerance, but the customer must see the 12.5 they asked for. For an
    /// indivisible product the two agree exactly.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuantityInUnitDisplay))]
    private decimal _quantityInUnit;

    /// <summary>Amount in the secondary unit without trailing zeros, so a line
    /// reads "12.72" and not "12.720".</summary>
    public string QuantityInUnitDisplay => QuantityInUnit == decimal.Truncate(QuantityInUnit)
        ? decimal.Truncate(QuantityInUnit).ToString(CultureInfo.InvariantCulture)
        : QuantityInUnit.ToString("0.######", CultureInfo.InvariantCulture);
}
