using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

public class QueueNumberOptionsTest
{
    [Fact]
    public void DefaultMatchesTheConstantsThePoolShippedWith()
    {
        var options = QueueNumberOptions.Default(tillIndex: 2, secret: "s");

        Assert.Equal(2, options.TillIndex);
        Assert.Equal(5, options.TillCount);
        Assert.Equal(100, options.Min);
        Assert.Equal(999, options.Max);
        Assert.True(options.Shuffle);
        Assert.Equal(string.Empty, options.Prefix);
        Assert.Equal("s", options.Secret);
        Assert.True(options.ShapesTheLegacyPool);
    }

    [Theory]
    [InlineData(4, 100, 999, true, false)]   // TillCount
    [InlineData(5, 1, 999, true, false)]     // Min
    [InlineData(5, 100, 500, true, false)]   // Max
    [InlineData(5, 100, 999, false, false)]  // Shuffle
    [InlineData(5, 100, 999, true, true)]
    public void OnlyTheShippedShapeCountsAsLegacy(
        int tillCount, int min, int max, bool shuffle, bool expected)
    {
        var options = new QueueNumberOptions(0, tillCount, min, max, shuffle, "", "s");

        Assert.Equal(expected, options.ShapesTheLegacyPool);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(9, 9)]
    [InlineData(50, 9)]
    [InlineData(-3, 1)]
    public void TillCountIsClampedToOneThroughNine(int raw, int expected)
    {
        Assert.Equal(expected, QueueNumberOptions.ClampTillCount(raw));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    [InlineData(9999, 9999)]
    [InlineData(10000, 9999)]
    [InlineData(-7, 1)]
    public void MinIsClampedToOneThroughFourDigits(int raw, int expected)
    {
        Assert.Equal(expected, QueueNumberOptions.ClampMin(raw));
    }

    [Theory]
    [InlineData(999, 100, 999)]
    [InlineData(50, 100, 100)]    // below Min → Min
    [InlineData(99999, 100, 9999)]
    [InlineData(0, 1, 1)]
    public void MaxIsClampedBetweenMinAndFourDigits(int raw, int min, int expected)
    {
        Assert.Equal(expected, QueueNumberOptions.ClampMax(raw, min));
    }

    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(4, 5, 4)]
    [InlineData(9, 5, 4)]
    [InlineData(-3, 5, 0)]
    [InlineData(3, 2, 1)]
    [InlineData(0, 1, 0)]
    public void TillIndexIsClampedIntoTheTillCount(int raw, int tillCount, int expected)
    {
        Assert.Equal(expected, QueueNumberOptions.ClampTillIndex(raw, tillCount));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  A- ", "A-")]
    [InlineData("ABCD", "ABC")]
    [InlineData("Кас", "Кас")]
    [InlineData("  ABCDEF  ", "ABC")]
    public void PrefixIsTrimmedAndCutToThreeCharacters(string? raw, string expected)
    {
        Assert.Equal(expected, QueueNumberOptions.NormalizePrefix(raw));
    }
}
