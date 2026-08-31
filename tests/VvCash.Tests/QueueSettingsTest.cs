using System.IO;
using VvCash.Services;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

public class QueueSettingsTest
{
    private static string WriteSettings(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vv-settings-{Path.GetRandomFileName()}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void AnUntouchedRegisterHasTheQueueSwitchedOff()
    {
        IQueueSettings settings = new SettingsService(WriteSettings("{}"));

        Assert.Equal(QueueRole.Off, settings.QueueRole);
        Assert.Equal(8770, settings.QueuePort);
        Assert.Equal(0, settings.TillIndex);
    }

    [Fact]
    public void PortZeroOrNegativeReadsAsTheDefault()
    {
        IQueueSettings settings = new SettingsService(WriteSettings("""{ "QueuePort": 0 }"""));

        Assert.Equal(8770, settings.QueuePort);
    }

    [Fact]
    public void TillIndexIsClampedIntoTheSlice()
    {
        IQueueSettings tooBig = new SettingsService(WriteSettings("""{ "TillIndex": 9 }"""));
        IQueueSettings negative = new SettingsService(WriteSettings("""{ "TillIndex": -3 }"""));

        Assert.Equal(4, tooBig.TillIndex);
        Assert.Equal(0, negative.TillIndex);
    }

    [Fact]
    public void RoleIsReadAsAName()
    {
        IQueueSettings settings = new SettingsService(WriteSettings("""{ "QueueRole": "Server" }"""));

        Assert.Equal(QueueRole.Server, settings.QueueRole);
    }
}
