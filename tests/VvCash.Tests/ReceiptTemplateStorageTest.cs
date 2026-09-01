using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VvCash.Models.Receipt;
using VvCash.Services.Data;
using Xunit;

namespace VvCash.Tests;

public class ReceiptTemplateStorageTest : IDisposable
{
    // One instance of this test class per [Fact] (xUnit default), so this only ever
    // collects the db file(s) that fact itself created via NewStorage().
    private readonly List<string> _dbPaths = new();

    [Fact]
    public async Task RawTemplate_RoundTrips()
    {
        var storage = await NewStorage();
        var json = """{"version":1,"width":42,"blocks":[]}""";

        await storage.SaveReceiptTemplateAsync(json);

        Assert.Equal(json, await storage.GetReceiptTemplateAsync());
    }

    [Fact]
    public async Task RawTemplate_IsEmpty_WhenNothingWasEverSynced()
    {
        var storage = await NewStorage();

        // Assert.Equal(string.Empty, ...), not IsNullOrEmpty: the interface promises
        // Task<string> under nullable — i.e. never null. IsNullOrEmpty is true for
        // null too, so it would not catch a regression that returns null instead of "".
        Assert.Equal(string.Empty, await storage.GetReceiptTemplateAsync());
    }

    [Fact]
    public async Task Logo_IsEmpty_WhenNothingWasEverSynced()
    {
        var storage = await NewStorage();

        Assert.Equal(string.Empty, await storage.GetReceiptLogoAsync());
    }

    [Fact]
    public async Task ACorruptCachedTemplate_ParsesToTheDefault_RatherThanThrowing()
    {
        // Опция receiptTemplate засеяна в 2019 и шесть лет рендерилась текстовым
        // полем — в configs.val у живого тенанта может лежать что угодно.
        var storage = await NewStorage();
        await storage.SaveReceiptTemplateAsync("{это не json");

        // Сравнение сериализованного JSON, а НЕ Assert.Same: ReceiptTemplate.Default
        // это фабрика (`=> new()`), новый объект на каждое обращение, поэтому
        // ссылочная тождественность здесь никогда не выполнится. Тот же приём
        // используют тесты ReceiptTemplateTest.
        var parsed = ReceiptTemplate.Parse(await storage.GetReceiptTemplateAsync());

        Assert.Equal(
            JsonSerializer.Serialize(ReceiptTemplate.Default, ReceiptTemplate.Options),
            JsonSerializer.Serialize(parsed, ReceiptTemplate.Options));
    }

    [Fact]
    public async Task Logo_RoundTrips()
    {
        var storage = await NewStorage();

        await storage.SaveReceiptLogoAsync("AAECAw==");

        Assert.Equal("AAECAw==", await storage.GetReceiptLogoAsync());
    }

    /// <summary>Template and logo are two keys sharing the same Settings table and the
    /// same SaveSettingAsync/GetSettingAsync helpers — a copy-paste that hardcodes the
    /// wrong key string on one side would make them silently overwrite each other, and
    /// no other test here would notice: every other test saves only one of the two.</summary>
    [Fact]
    public async Task Template_And_Logo_DoNotShareStorage()
    {
        var storage = await NewStorage();
        var json = """{"version":1,"width":42,"blocks":[]}""";
        var logo = "AAECAw==";

        await storage.SaveReceiptTemplateAsync(json);
        await storage.SaveReceiptLogoAsync(logo);

        Assert.Equal(json, await storage.GetReceiptTemplateAsync());
        Assert.Equal(logo, await storage.GetReceiptLogoAsync());
    }

    /// <summary>InitializeAsync обязателен: именно он создаёт таблицу Settings,
    /// из которой всё это читается. Без него тест падает не про то.</summary>
    private async Task<OfflineStorageService> NewStorage()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vvcash-receipt-{Guid.NewGuid():N}.db");
        _dbPaths.Add(dbPath);

        var storage = new OfflineStorageService(dbPath);
        await storage.InitializeAsync();
        return storage;
    }

    /// <summary>Same pooling gotcha OfflineStorageServiceTest.Dispose documents:
    /// Microsoft.Data.Sqlite pools connections by connection string, so the file
    /// outlives a disposed SqliteConnection until the pool is cleared, and deleting it
    /// first fails silently on Windows (file still in use).</summary>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var dbPath in _dbPaths)
        {
            foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm", dbPath + "-journal" })
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort cleanup */ }
            }
        }
    }
}
