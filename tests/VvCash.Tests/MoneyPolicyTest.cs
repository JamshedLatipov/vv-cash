using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

/// <summary>Offline pricing rounds with the store's policy, so these must match
/// cloudmarket-server's base.Quantize for the same mode — a mismatch shows up as
/// an offline total a minor unit away from the online one.</summary>
public class MoneyPolicyTest
{
    private static MoneyPolicy Policy(string mode, int scale = 2)
        => new() { Mode = mode, Scale = scale };

    [Theory]
    // HALF_UP is half away from zero, matching shopspring's Round.
    [InlineData("HALF_UP", 1.005, 1.01)]
    [InlineData("HALF_UP", 1.004, 1.00)]
    [InlineData("HALF_UP", -1.005, -1.01)]
    // BANK breaks ties to even.
    [InlineData("BANK", 1.005, 1.00)]
    [InlineData("BANK", 1.015, 1.02)]
    // UP goes away from zero on ANY remainder, not just at the midpoint.
    [InlineData("UP", 1.001, 1.01)]
    [InlineData("UP", -1.001, -1.01)]
    [InlineData("UP", 1.010, 1.01)]
    // DOWN truncates toward zero.
    [InlineData("DOWN", 1.009, 1.00)]
    [InlineData("DOWN", -1.009, -1.00)]
    // CEIL toward +inf, FLOOR toward -inf.
    [InlineData("CEIL", 1.001, 1.01)]
    [InlineData("CEIL", -1.009, -1.00)]
    [InlineData("FLOOR", 1.009, 1.00)]
    [InlineData("FLOOR", -1.001, -1.01)]
    public void Round_MatchesTheServerMode(string mode, double value, double expected)
    {
        Assert.Equal((decimal)expected, Policy(mode).Round((decimal)value));
    }

    [Fact]
    public void Round_HonoursScale()
    {
        Assert.Equal(1m, Policy("HALF_UP", 0).Round(1.4m));
        Assert.Equal(2m, Policy("HALF_UP", 0).Round(1.5m));
        Assert.Equal(1.235m, Policy("HALF_UP", 3).Round(1.2345m));
    }

    [Fact]
    public void Round_UnknownModeFallsBackToHalfUp()
    {
        // A store setting the register does not understand must not stop a sale.
        Assert.Equal(1.01m, Policy("SOMETHING_NEW").Round(1.005m));
    }

    [Fact]
    public void Default_IsTwoPlacesHalfUp()
    {
        var p = MoneyPolicy.Default;

        Assert.Equal(2, p.Scale);
        Assert.Equal("HALF_UP", p.Mode);
    }
}
