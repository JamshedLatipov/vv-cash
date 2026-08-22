using System.Globalization;
using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

public class QuantityFormatTest
{
    // Входы строками, а не decimal-литералами: атрибут не принимает decimal, и
    // xUnit довёз бы их как double — а «1.400» и «1.4» это один и тот же double.
    // Случай с хвостовыми нулями, ради которого строка и заведена, потерял бы
    // масштаб ещё до входа в тест и проверял бы ровно то же, что соседний.
    [Theory]
    [InlineData("2.0", "2")]
    [InlineData("1.4", "1.4")]
    [InlineData("1.400", "1.4")]
    [InlineData("0.5", "0.5")]
    [InlineData("53", "53")]
    public void Display_DropsTrailingZeroesButKeepsRealFractions(string input, string expected)
    {
        var value = decimal.Parse(input, CultureInfo.InvariantCulture);

        Assert.Equal(expected, QuantityFormat.Display(value, "0.###"));
    }

    [Fact]
    public void Display_UsesTheInvariantSeparator()
    {
        // Точка, а не запятая, на любой локали ОС: тот же чек не должен печататься
        // по-разному на соседних кассах.
        Assert.Equal("1.4", QuantityFormat.Display(1.4m, "0.###"));
    }

    [Fact]
    public void Display_HonoursTheRequestedPrecision()
    {
        Assert.Equal("12.720001", QuantityFormat.Display(12.720001m, "0.######"));
        Assert.Equal("12.72", QuantityFormat.Display(12.720001m, "0.###"));
    }
}
