using System.Collections.Generic;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

// The server sends an ISO code; the cashier reads "смн.". The dictionary is the
// locale's business, the fallbacks are this class's.
public class CurrencyLabelTest
{
    private static readonly Dictionary<string, string> Ru = new()
    {
        ["Currency_TJS"] = "смн.",
    };

    private static string? Lookup(string key) => Ru.TryGetValue(key, out var v) ? v : null;

    [Fact]
    public void KnownCode_ReadsAsTheLocaleShortForm()
    {
        Assert.Equal("смн.", CurrencyLabel.For("TJS", Lookup));
    }

    [Fact]
    public void CodeIsMatchedWhateverItsCase()
    {
        // The back office lets a person type the code; "tjs" is still somoni.
        Assert.Equal("смн.", CurrencyLabel.For(" tjs ", Lookup));
    }

    [Fact]
    public void UnknownCode_ReadsAsTheCodeItself()
    {
        Assert.Equal("AED", CurrencyLabel.For("AED", Lookup));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoCode_HasNoLabel(string? code)
    {
        Assert.Null(CurrencyLabel.For(code, Lookup));
    }
}
