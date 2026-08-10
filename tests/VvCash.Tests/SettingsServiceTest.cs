using System;
using System.IO;
using System.Text.Json;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

/// <summary>SettingsService reads and writes the one file that makes a register a
/// register — backend URL, cash token, printers. Nothing exercised it before, because it
/// hardcoded its own path under LocalApplicationData; it now takes an optional one, the
/// same way OfflineStorageService does for the same reason.</summary>
public class SettingsServiceTest : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public SettingsServiceTest()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"vvcash-settings-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort cleanup */ }
    }

    [Fact]
    public void RoundTripsTheRegisterConfiguration()
    {
        var service = new SettingsService(_path)
        {
            BackendUrl = "https://api.example.test/v1/",
            CashRegisterToken = "cash-token",
        };
        service.Save();

        var reopened = new SettingsService(_path);

        Assert.Equal("https://api.example.test/v1/", reopened.BackendUrl);
        Assert.Equal("cash-token", reopened.CashRegisterToken);
    }

    [Fact]
    public void CorruptSettingsFile_IsKeptAsideRatherThanOverwritten()
    {
        // A settings file that will not parse — a torn write, a half-flushed disk on
        // power loss — used to be swallowed and replaced with defaults on the next Save.
        // The register came up blank: no backend URL, no cash token, and nothing on
        // screen or on disk saying why, or what the values had been.
        File.WriteAllText(_path, "{ this is not json");

        var service = new SettingsService(_path);
        service.Save();

        Assert.Equal(string.Empty, service.BackendUrl); // defaults, as before
        var kept = Directory.GetFiles(_dir, "settings.json.corrupt-*");
        Assert.Single(kept);
        Assert.Equal("{ this is not json", File.ReadAllText(kept[0]));
    }

    [Fact]
    public void ValidSettingsFile_IsNotKeptAside()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(new SettingsData { BackendUrl = "https://a.test/" }));

        var service = new SettingsService(_path);

        Assert.Equal("https://a.test/", service.BackendUrl);
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
    }
}
