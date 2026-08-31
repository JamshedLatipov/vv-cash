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

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void PortZeroOrNegativeReadsAsTheDefault(int badPort)
    {
        IQueueSettings settings = new SettingsService(WriteSettings($$"""{ "QueuePort": {{badPort}} }"""));

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

    /// <summary>The converter shipped with no tests at all, and the guard is
    /// load-bearing: a bad QueueRole value must fall back to Off without
    /// taking Deserialize&lt;SettingsData&gt; down with it — Load()'s catch-all
    /// would otherwise reset the whole file, losing BackendUrl and every
    /// token along with it. The BackendUrl assertion is the point of each
    /// case, not a formality: QueueRole alone would read Off even in a world
    /// where the file had been wiped, so only the sibling proves the file
    /// survived.</summary>
    [Theory]
    [InlineData("\"Srver\"")] // mistyped name
    [InlineData("5")]         // number
    [InlineData("null")]      // null
    [InlineData("[]")]        // array
    [InlineData("{}")]        // object
    public void AMisshapenRoleFallsBackToOffWithoutLosingItsNeighbours(string badRole)
    {
        var json = $$"""{ "QueueRole": {{badRole}}, "BackendUrl": "http://shop.example" }""";
        var settings = new SettingsService(WriteSettings(json));

        Assert.Equal(QueueRole.Off, settings.QueueRole);
        Assert.Equal("http://shop.example", settings.BackendUrl);
    }
}
