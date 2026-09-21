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
/// discovered on the receipt. The same goes the other way in money mode: 50
/// somoni at 30 per kilogram is 1.666 kg and bills 49.98, and that cut is
/// shown before Apply, not on the receipt.</summary>
public partial class QuantityPadViewModel : ObservableObject
{
    /// <summary>Where a money-derived amount is cut: scales weigh in grams,
    /// and the cashier weighs out exactly the figure on screen.</summary>
    private const decimal WeightStep = 0.001m;

    private readonly CartItem _item;

    public QuantityPadViewModel(CartItem item)
    {
        _item = item;
        // Never Money: that is a one-off gesture, and the line stores a
        // quantity, not the sum it came from.
        _mode = item.EnteredInUnit && item.Product.HasSecondaryUnit ? PadMode.Unit : PadMode.Pieces;
        _input = _mode == PadMode.Unit ? Seed(item.QuantityInUnit) : Seed(item.Quantity);
    }

    public CartItem Item => _item;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewQuantity), nameof(PreviewQuantityInUnit),
        nameof(PreviewTotal), nameof(PreviewText), nameof(IsRounded), nameof(IsRoundedDown), nameof(IsValid))]
    private string _input = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPiecesMode), nameof(IsUnitMode), nameof(IsMoneyMode),
        nameof(PriceInSelectedUnit), nameof(UnitLabel), nameof(PriceUnitLabel),
        nameof(PreviewQuantity), nameof(PreviewQuantityInUnit), nameof(PreviewTotal),
        nameof(PreviewText), nameof(IsRounded), nameof(IsRoundedDown), nameof(IsValid))]
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

    /// <summary>The amount the pad resolves to, in <see cref="PriceUnitLabel"/>'s
    /// unit: the typed figure, or in money mode the sum divided by the price
    /// and floored to the gram. Null when there is nothing valid to resolve.
    /// Shared by the preview and <see cref="Commit"/> so the two cannot
    /// disagree.</summary>
    private decimal? Amount
    {
        get
        {
            var typed = Parsed;
            if (typed is null) return null;
            if (Mode != PadMode.Money) return typed;
            if (!CanEnterMoney) return null;

            // Floor, not round: the customer named the sum, so the line must
            // not come out above it. 50 / 30 → 1.666 kg → 49.98.
            //
            // One division, not two. PriceInSelectedUnit is itself a rounded
            // quotient (10 / 0.6 = 16.666…67), and dividing by it again puts
            // 50 × 0.6 / 10 = 3 at 2.999…, which the floor then cuts to 2.999.
            var amount = FloorToWeight(ScaledSum(typed.Value) / _item.UnitPrice);
            return amount > 0m ? amount : null;
        }
    }

    private static decimal FloorToWeight(decimal value)
        => decimal.Truncate(value / WeightStep) * WeightStep;

    /// <summary>The stored quantity as the box should open on it: without
    /// the trailing zeros a money-derived amount carries (2.000 kg reads
    /// "2", like the cart line), but with every significant digit — a format
    /// mask would cut 52.083333 pieces to "52.083", and an untouched Apply
    /// would then rewrite the line. Dividing by 1.000… is decimal's way of
    /// dropping scale without touching value.</summary>
    private static string Seed(decimal value)
        => (value / 1.000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    /// <summary>The typed sum expressed against the per-piece price, so that
    /// one division by the line's unit price gives the amount in
    /// <see cref="PriceUnitLabel"/>'s unit: 50 somoni of 0.5 kg packs at 15
    /// → 25 → 1.666 kg. An exact product, so that division is the only
    /// rounding step.</summary>
    private decimal ScaledSum(decimal money)
        => DerivesInUnit ? money * _item.Product.UnitFactor : money;

    /// <summary>Whether the current input can be committed. Rejects an empty or
    /// unparseable box, a non-positive amount, and a fractional piece count on
    /// an indivisible product — half a tile does not exist. In money mode it
    /// also rejects a product the mode is not offered for and a sum below one
    /// gram — both surface as a null <see cref="Amount"/>.</summary>
    public bool IsValid
    {
        get
        {
            var amount = Amount;
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
            var amount = Amount;
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
            var amount = Amount;
            if (amount is null || !_item.Product.HasSecondaryUnit) return 0m;
            if (!DerivesInUnit) return UnitConverter.ToUnit(amount.Value, _item.Product.UnitFactor);
            return UnitConverter.ToBase(
                amount.Value, _item.Product.UnitFactor, _item.Product.IsDivisible).QuantityInUnit;
        }
    }

    public decimal PreviewTotal => PreviewQuantity * _item.UnitPrice;

    /// <summary>Whether the entered amount was rounded up to a whole piece.
    /// Drives the callout in the pad, because this is the case where the
    /// customer pays for more than they asked for. Always false in money
    /// mode: a divisible product never rounds up, and the cut the other way
    /// has its own flag, <see cref="IsRoundedDown"/>.</summary>
    public bool IsRounded
    {
        get
        {
            var amount = Amount;
            if (amount is null || Mode != PadMode.Unit) return false;
            return PreviewQuantityInUnit != amount.Value;
        }
    }

    /// <summary>Whether the derived weight was cut below the sum the customer
    /// named — the money-mode counterpart of <see cref="IsRounded"/>. 50 / 30
    /// = 1.666 kg bills 49.98, and the cashier should see that before the
    /// receipt does.</summary>
    public bool IsRoundedDown
    {
        get
        {
            var amount = Amount;
            if (Mode != PadMode.Money || amount is null) return false;
            // On the weight, not on PreviewTotal: that goes through the
            // six-decimal piece count, which lands 1.000 kg of 0.3 kg packs
            // at 9.999999 and flags a cut that never happened. Both sides
            // here are exact products.
            return amount.Value * _item.UnitPrice < ScaledSum(Parsed!.Value);
        }
    }

    /// <summary>Through <see cref="QuantityFormat"/>, not a raw ToString: a
    /// money-derived amount carries three decimals even when whole, and the
    /// cart line the cashier is about to see trims them.</summary>
    public string PreviewText => _item.Product.HasSecondaryUnit
        ? $"→ {QuantityFormat.Display(PreviewQuantity, "0.###")} шт = " +
          $"{QuantityFormat.Display(PreviewQuantityInUnit, "0.######")} {_item.Product.UnitShortName} · " +
          $"{PreviewTotal.ToString("F2", CultureInfo.InvariantCulture)}"
        : $"→ {QuantityFormat.Display(PreviewQuantity, "0.###")} шт · " +
          $"{PreviewTotal.ToString("F2", CultureInfo.InvariantCulture)}";

    public void Append(string digit) => Input += digit;

    public void Backspace()
    {
        if (Input.Length > 0) Input = Input[..^1];
    }

    public void Clear() => Input = string.Empty;

    /// <summary>Writes the pad's result back through the cart, which is what
    /// recomputes totals and re-prices the cart. In money mode the line is
    /// set from the derived amount; the sum itself is not kept — the line
    /// stores a quantity, and reopening the pad shows that quantity.</summary>
    public void Commit(ICartService cart)
    {
        var amount = Amount;
        if (amount is null || !IsValid) return;

        _item.EnteredInUnit = DerivesInUnit;
        if (DerivesInUnit) cart.SetQuantityInUnit(_item, amount.Value);
        else cart.SetQuantity(_item, amount.Value);
    }
}
