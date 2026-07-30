using System;
using System.Globalization;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

// The server re-derives every line it is sent: it rejects the document unless
// |quantity_in_unit - quantity * factor| stays inside ToleranceFor(factor).
// A register that rounds even slightly differently gets its own honest,
// already-printed receipts refused, so these cases are pinned against the
// server's units.ConvertToBase rather than against intuition.
public class UnitConverterTest
{
    private static decimal D(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);

    [Theory]
    // Divisible: pieces are the derived figure, the entered amount is kept as-is.
    [InlineData("12.5", "0.24", true, "52.083333", "12.5")]
    [InlineData("3.75", "2.5", true, "1.5", "3.75")]
    // Indivisible: pieces round UP and the unit amount is recomputed from them,
    // because the customer is charged for whole tiles.
    [InlineData("12.5", "0.24", false, "53", "12.72")]
    // Exact multiple must NOT round up - that would bill one tile too many.
    [InlineData("12.0", "0.24", false, "50", "12.0")]
    public void ToBase_MatchesServerConversion(
        string amount, string factor, bool isDivisible, string wantQty, string wantQtyInUnit)
    {
        var (qty, qtyInUnit) = UnitConverter.ToBase(D(amount), D(factor), isDivisible);

        Assert.Equal(D(wantQty), qty);
        Assert.Equal(D(wantQtyInUnit), qtyInUnit);
    }

    [Theory]
    [InlineData("12.5", "0.24", true)]
    [InlineData("3.75", "2.5", true)]
    [InlineData("12.5", "0.24", false)]
    [InlineData("12.0", "0.24", false)]
    [InlineData("0.0000025", "1", true)]
    public void ToBase_SatisfiesServerSnapshotTolerance(string amount, string factor, bool isDivisible)
    {
        var f = D(factor);
        var (qty, qtyInUnit) = UnitConverter.ToBase(D(amount), f, isDivisible);

        // units.ToleranceFor: max(0.001, factor * 1e-6).
        var tolerance = Math.Max(0.001m, f * 0.000001m);
        Assert.True(Math.Abs(qtyInUnit - qty * f) <= tolerance,
            $"snapshot drift {Math.Abs(qtyInUnit - qty * f)} exceeds tolerance {tolerance}");
    }

    [Fact]
    public void ToBase_RoundsHalfAwayFromZero_NotBankers()
    {
        // 0.0000025 sits exactly on the 6th-decimal midpoint with an even digit
        // before it, which is the only place the two rounding modes disagree:
        // the server's DivRound gives 0.000003, .NET's default MidpointRounding
        // .ToEven would give 0.000002.
        var (qty, _) = UnitConverter.ToBase(D("0.0000025"), 1m, isDivisible: true);

        Assert.Equal(D("0.000003"), qty);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void ToBase_RejectsNonPositiveFactor(string factor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UnitConverter.ToBase(1m, D(factor), isDivisible: true));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void ToBase_RejectsNonPositiveAmount(string amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UnitConverter.ToBase(D(amount), 0.24m, isDivisible: true));
    }

    [Fact]
    public void ToUnit_IsTheReverseView()
    {
        Assert.Equal(12.72m, UnitConverter.ToUnit(53m, 0.24m));
    }
}
