using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VvCash.Models.Receipt;
using VvCash.Services;
using VvCash.Services.Data;
using Xunit;

namespace VvCash.Tests;

/// <summary>Same reproduction shape as ReceiptTemplateStorageTest: a real SQLite
/// file, not a fake, because the bug this locks in is specific to the real
/// OfflineStorageService — GetSettingAsync runs a bare SELECT against the
/// Settings table with no existence check, so it throws SqliteException("no
/// such table: Settings") until InitializeAsync has run once.</summary>
public class ReceiptTemplateServiceTest : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vvcash-template-svc-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task RefreshAsync_OnAFreshProfileWithNoSettingsTable_DoesNotThrowAndKeepsTheDefault()
    {
        // Reproduces the crash a fresh install/cleared profile hits: App.axaml.cs
        // resolves IReceiptTemplateService and refreshes it before any PosViewModel
        // has ever been constructed, and PosViewModel.InitializeAsync is the only
        // other place that used to call OfflineStorageService.InitializeAsync
        // (which is what creates the Settings table in the first place). No
        // InitializeAsync call here on purpose -- the table must not exist yet.
        var storage = new OfflineStorageService(_dbPath);
        var svc = new ReceiptTemplateService(storage);

        await svc.RefreshAsync();

        // Structural comparison, not Assert.Same: ReceiptTemplate.Default is a
        // factory (`=> new()`), a fresh instance on every access.
        Assert.Equal(
            JsonSerializer.Serialize(ReceiptTemplate.Default, ReceiptTemplate.Options),
            JsonSerializer.Serialize(svc.Current, ReceiptTemplate.Options));
        Assert.Equal(string.Empty, svc.Logo);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _dbPath + "-journal" })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort cleanup */ }
        }
    }
}
