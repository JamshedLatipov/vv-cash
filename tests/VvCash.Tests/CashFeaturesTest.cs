using System.Collections.Generic;
using VvCash.Constants;
using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

public class CashFeaturesTest
{
    [Fact]
    public void IsEnabled_UnknownCode_ReturnsTrue()
    {
        // The register must stay fully functional when it has never reached the
        // server, and when the server omitted a code it could not resolve. This
        // is the single place the "enabled by default" rule lives.
        var features = new CashFeatures();

        Assert.True(features.IsEnabled(CashFeatureCodes.Returns));
        Assert.True(features.IsEnabled("something_added_next_year"));
    }

    [Fact]
    public void IsEnabled_ExplicitFalse_ReturnsFalse()
    {
        var features = new CashFeatures
        {
            Flags = new Dictionary<string, bool> { [CashFeatureCodes.Returns] = false }
        };

        Assert.False(features.IsEnabled(CashFeatureCodes.Returns));
    }

    [Fact]
    public void IsEnabled_ExplicitTrue_ReturnsTrue()
    {
        var features = new CashFeatures
        {
            Flags = new Dictionary<string, bool> { [CashFeatureCodes.ParkedSales] = true }
        };

        Assert.True(features.IsEnabled(CashFeatureCodes.ParkedSales));
    }

    [Fact]
    public void IsEnabled_OtherCodesConfigured_LeavesThisOneEnabled()
    {
        // One flag switched off must not drag the other seven with it.
        var features = new CashFeatures
        {
            Flags = new Dictionary<string, bool> { [CashFeatureCodes.Returns] = false }
        };

        Assert.True(features.IsEnabled(CashFeatureCodes.MixedPayment));
    }

    [Fact]
    public void Default_IsEverythingEnabled()
    {
        var features = CashFeatures.Default;

        Assert.Empty(features.Flags);
        Assert.True(features.IsEnabled(CashFeatureCodes.SellerSwitch));
    }
}
