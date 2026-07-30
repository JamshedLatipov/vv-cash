using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

public class ProductUnitTest
{
    [Fact]
    public void HasSecondaryUnit_IsFalse_ForAPieceOnlyProduct()
    {
        Assert.False(new Product { Id = "p1" }.HasSecondaryUnit);
    }

    [Fact]
    public void HasSecondaryUnit_IsTrue_WhenIdAndFactorAreBothSet()
    {
        var p = new Product { Id = "p1", UnitId = "u1", UnitFactor = 0.24m };

        Assert.True(p.HasSecondaryUnit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void HasSecondaryUnit_IsFalse_WhenTheFactorIsNotPositive(int factor)
    {
        // A filled unit id with a zero or negative factor is a broken product
        // card. Reading it as piece-only keeps the register selling instead of
        // dividing by zero at the till.
        var p = new Product { Id = "p1", UnitId = "u1", UnitFactor = factor };

        Assert.False(p.HasSecondaryUnit);
    }
}
