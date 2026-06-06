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
}
