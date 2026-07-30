using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class SettingsDefaultsTest
{
    [Fact]
    public void PostReturnFlags_DefaultToTrue()
    {
        var data = new SettingsData();
        Assert.True(data.ReturnOpenCashDrawer);
        Assert.True(data.ReturnPrintReceipt);
    }

    [Fact]
    public void ExchangePayoutCategory_DefaultsToUnset()
    {
        // Unset is the only safe default: the exchange screen refuses while it is
        // empty, whereas guessing a category would file real money under the wrong
        // heading in every store that never configured one.
        Assert.Equal(string.Empty, new SettingsData().ExchangePayoutCategoryId);
    }
}
