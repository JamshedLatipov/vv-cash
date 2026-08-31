using System.IO;
using System.Threading.Tasks;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

public class QueueStorageTest
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");

    [Fact]
    public async Task InitializeIsIdempotent()
    {
        var path = TempDb();
        var storage = new QueueStorage(path);

        await storage.InitializeAsync();
        await storage.InitializeAsync();

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task StateSurvivesReopening()
    {
        var path = TempDb();
        var first = new QueueStorage(path);
        await first.InitializeAsync();
        await first.SetStateAsync("Day", "2026-08-31");

        var second = new QueueStorage(path);
        await second.InitializeAsync();

        Assert.Equal("2026-08-31", await second.GetStateAsync("Day"));
    }

    [Fact]
    public async Task MissingStateReadsAsNull()
    {
        var storage = new QueueStorage(TempDb());
        await storage.InitializeAsync();

        Assert.Null(await storage.GetStateAsync("Day"));
    }
}
