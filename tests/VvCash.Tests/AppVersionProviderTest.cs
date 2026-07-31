using System;
using VvCash.Services.Update;
using Xunit;

namespace VvCash.Tests;

public class AppVersionProviderTest
{
    [Theory]
    [InlineData(1, 0, 0, 0, "1.0.0")]
    [InlineData(1, 2, 3, 4, "1.2.3")]
    [InlineData(2, 5, -1, -1, "2.5.0")]
    public void NormalizeTrimsToThreeParts(int major, int minor, int build, int revision, string expected)
    {
        var raw = revision >= 0
            ? new Version(major, minor, build, revision)
            : build >= 0 ? new Version(major, minor, build) : new Version(major, minor);

        Assert.Equal(expected, AppVersion.Normalize(raw).ToString());
    }

    [Fact]
    public void AssemblyProviderReportsAThreePartVersion()
    {
        var provider = new AssemblyAppVersionProvider();

        // Whatever the test host reports, the provider must hand back exactly three
        // components — everything downstream formats and compares on that assumption.
        Assert.True(provider.Current.Build >= 0);
        Assert.Equal(-1, provider.Current.Revision);
    }
}
