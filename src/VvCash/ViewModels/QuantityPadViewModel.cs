using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using VvCash.Models;
using VvCash.Services;

namespace VvCash.ViewModels;

/// <summary>Backs the quantity pad: the cashier types an amount, and the pad
/// shows what it becomes before anything is committed.
///
/// The live preview exists for one reason. An indivisible product rounds up to
/// the next whole piece, so 12.5 m² of tile bills as 12.72 m². That is the
/// customer's money, and it must be on screen before the line is confirmed, not
/// discovered on the receipt.</summary>
public partial class QuantityPadViewModel : ObservableObject
{
    private readonly CartItem _item;

    public QuantityPadViewModel(CartItem item)
    {
        _item = item;
        _enteredInUnit = item.EnteredInUnit && item.Product.HasSecondaryUnit;
        _input = _enteredInUnit
            ? item.QuantityInUnit.ToString(CultureInfo.InvariantCulture)
            : item.Quantity.ToString(CultureInfo.InvariantCulture);
    }

    public CartItem Item => _item;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewQuantity), nameof(PreviewQuantityInUnit),
        nameof(PreviewTotal), nameof(PreviewText), nameof(IsRounded), nameof(IsValid))]
    private string _input = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriceInSelectedUnit), nameof(UnitLabel), nameof(PreviewQuantity),
        nameof(PreviewQuantityInUnit), nameof(PreviewTotal), nameof(PreviewText),
        nameof(IsRounded), nameof(IsValid))]
    private bool _enteredInUnit;

    /// <summary>Whether the piece/unit toggle is offered at all. A piece-only
    /// product has nothing to switch to.</summary>
    public bool CanSwitchUnit => _item.Product.HasSecondaryUnit;

    public string UnitLabel => EnteredInUnit ? _item.Product.UnitShortName : "шт";

    /// <summary>Price expressed in whichever unit is selected, so the ticket
    /// reads "416.67 / м²" while the cashier is typing square metres.
    ///
    /// Built on the line's own unit price, not the cached catalogue one: once a
    /// server quote prices the line that is what the customer pays, and showing
    /// the stale figure here would contradict the cart total.</summary>
    public decimal PriceInSelectedUnit => EnteredInUnit
        ? _item.UnitPrice / _item.Product.UnitFactor
        : _item.UnitPrice;

    private decimal? Parsed =>
        decimal.TryParse(Input, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0m
            ? v
            : null;

    /// <summary>Whether the current input can be committed. Rejects an empty or
    /// unparseable box, a non-positive amount, and a fractional piece count on
    /// an indivisible product — half a tile does not exist.</summary>
    public bool IsValid
    {
        get
        {
            var amount = Parsed;
            if (amount is null) return false;
            if (!EnteredInUnit && !_item.Product.IsDivisible && amount != decimal.Truncate(amount.Value))
                return false;
            return true;
        }
    }

    public decimal PreviewQuantity
    {
        get
        {
            var amount = Parsed;
            if (amount is null) return 0m;
            if (!EnteredInUnit) return amount.Value;
            return UnitConverter.ToBase(
                amount.Value, _item.Product.UnitFactor, _item.Product.IsDivisible).Quantity;
        }
    }

    public decimal PreviewQuantityInUnit
    {
        get
        {
            var amount = Parsed;
            if (amount is null || !_item.Product.HasSecondaryUnit) return 0m;
            if (!EnteredInUnit) return UnitConverter.ToUnit(amount.Value, _item.Product.UnitFactor);
            return UnitConverter.ToBase(
                amount.Value, _item.Product.UnitFactor, _item.Product.IsDivisible).QuantityInUnit;
        }
    }

    public decimal PreviewTotal => PreviewQuantity * _item.UnitPrice;

    /// <summary>Whether the entered amount was rounded up to a whole piece.
    /// Drives the callout in the pad, because this is the case where the
    /// customer pays for more than they asked for.</summary>
    public bool IsRounded
    {
        get
        {
            var amount = Parsed;
            if (amount is null || !EnteredInUnit) return false;
            return PreviewQuantityInUnit != amount.Value;
        }
    }

    public string PreviewText => _item.Product.HasSecondaryUnit
        ? $"→ {PreviewQuantity} шт = {PreviewQuantityInUnit} {_item.Product.UnitShortName} · {PreviewTotal:F2}"
        : $"→ {PreviewQuantity} шт · {PreviewTotal:F2}";

    public void Append(string digit) => Input += digit;

    public void Backspace()
    {
        if (Input.Length > 0) Input = Input[..^1];
    }

    public void Clear() => Input = string.Empty;

    /// <summary>Writes the pad's result back through the cart, which is what
    /// recomputes totals and re-prices the cart.</summary>
    public void Commit(ICartService cart)
    {
        var amount = Parsed;
        if (amount is null || !IsValid) return;

        _item.EnteredInUnit = EnteredInUnit;
        if (EnteredInUnit) cart.SetQuantityInUnit(_item, amount.Value);
        else cart.SetQuantity(_item, amount.Value);
    }
}
