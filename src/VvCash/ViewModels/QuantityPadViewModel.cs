using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using VvCash.Models;
using VvCash.Services;

namespace VvCash.ViewModels;

/// <summary>Which figure the cashier is typing into the pad.</summary>
public enum PadMode
{
    /// <summary>A piece count.</summary>
    Pieces,

    /// <summary>An amount in the product's secondary unit — m², kg.</summary>
    Unit,

    /// <summary>Money: "50 somoni of nuggets". The pad derives the weight.</summary>
    Money,
}

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
        // Never Money: that is a one-off gesture, and the line stores a
        // quantity, not the sum it came from.
        _mode = item.EnteredInUnit && item.Product.HasSecondaryUnit ? PadMode.Unit : PadMode.Pieces;
        _input = _mode == PadMode.Unit
            ? item.QuantityInUnit.ToString(CultureInfo.InvariantCulture)
            : item.Quantity.ToString(CultureInfo.InvariantCulture);
    }

    public CartItem Item => _item;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewQuantity), nameof(PreviewQuantityInUnit),
        nameof(PreviewTotal), nameof(PreviewText), nameof(IsRounded), nameof(IsValid))]
    private string _input = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPiecesMode), nameof(IsUnitMode), nameof(IsMoneyMode),
        nameof(PriceInSelectedUnit), nameof(UnitLabel), nameof(PriceUnitLabel),
        nameof(PreviewQuantity), nameof(PreviewQuantityInUnit), nameof(PreviewTotal),
        nameof(PreviewText), nameof(IsRounded), nameof(IsValid))]
    private PadMode _mode;

    // One bool per segment, the way the discount modal's IsDiscountPercentMode /
    // IsDiscountAmountMode pair works. A RadioButton binds IsChecked two-way and
    // writes false to the segment it leaves, which must not move the mode.
    public bool IsPiecesMode { get => Mode == PadMode.Pieces; set { if (value) Mode = PadMode.Pieces; } }
    public bool IsUnitMode { get => Mode == PadMode.Unit; set { if (value) Mode = PadMode.Unit; } }
    public bool IsMoneyMode { get => Mode == PadMode.Money; set { if (value) Mode = PadMode.Money; } }

    /// <summary>Whether the piece/unit segment is offered at all. A piece-only
    /// product has nothing to switch to.</summary>
    public bool CanSwitchUnit => _item.Product.HasSecondaryUnit;

    /// <summary>Whether "sell for N" is offered. Only a divisible product can
    /// carry a derived fractional amount — 50 / 3 = 16.67 pieces does not
    /// exist — and a free one has nothing to divide by.</summary>
    public bool CanEnterMoney => _item.Product.IsDivisible && _item.UnitPrice > 0m;

    /// <summary>Whether any segment besides "шт" exists. With none, the whole
    /// selector is hidden: an inert control reads as broken.</summary>
    public bool HasModeSelector => CanSwitchUnit || CanEnterMoney;

    /// <summary>Whether the pad's result is expressed in the secondary unit.
    /// In money mode a product that has one derives in it regardless of
    /// SellInSecondaryUnit: the customer asking for "50 somoni of nuggets"
    /// thinks in kilograms, not packs.</summary>
    private bool DerivesInUnit => Mode switch
    {
        PadMode.Unit => true,
        PadMode.Money => _item.Product.HasSecondaryUnit,
        _ => false,
    };

    /// <summary>Label next to the typed figure: "3 шт", "12.5 м²", "50 сум".</summary>
    public string UnitLabel => Mode switch
    {
        PadMode.Unit => _item.Product.UnitShortName,
        PadMode.Money => "сум",
        _ => "шт",
    };

    /// <summary>Unit the entered figure resolves to, for the "price / unit"
    /// header. Differs from <see cref="UnitLabel"/> only in money mode, where
    /// the header must read "30.00 / кг" and not "30.00 / сум".</summary>
    public string PriceUnitLabel => DerivesInUnit ? _item.Product.UnitShortName : "шт";

    /// <summary>Price expressed in the unit the entry resolves to, so the ticket
    /// reads "416.67 / м²" while the cashier is typing square metres.
    ///
    /// Built on the line's own unit price, not the cached catalogue one: once a
    /// server quote prices the line that is what the customer pays, and showing
    /// the stale figure here would contradict the cart total.</summary>
    public decimal PriceInSelectedUnit => DerivesInUnit
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
            if (!DerivesInUnit && !_item.Product.IsDivisible && amount != decimal.Truncate(amount.Value))
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
            if (!DerivesInUnit) return amount.Value;
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
            if (!DerivesInUnit) return UnitConverter.ToUnit(amount.Value, _item.Product.UnitFactor);
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
            if (amount is null || !DerivesInUnit) return false;
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

        _item.EnteredInUnit = DerivesInUnit;
        if (DerivesInUnit) cart.SetQuantityInUnit(_item, amount.Value);
        else cart.SetQuantity(_item, amount.Value);
    }
}
