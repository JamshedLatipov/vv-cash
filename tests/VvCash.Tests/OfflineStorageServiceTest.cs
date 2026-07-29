using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VvCash.Constants;
using VvCash.Models;
using VvCash.Services.Data;
using Xunit;

namespace VvCash.Tests;

// Nothing in the existing suite exercises OfflineStorageService against a real
// SQLite file — every other test that needs IOfflineStorageService substitutes a
// hand-written in-memory fake (see SyncServiceTest.FakeStorage). That's fine for
// testing callers, but it means the SQLite read/write code itself — the decimal
// <-> REAL conversion, NULL vs. empty-string handling, the delete-then-insert
// replace semantics — has never actually been run. A temp-file SQLite database is
// workable here: Microsoft.Data.Sqlite is already a transitive package reference
// via the ProjectReference to VvCash.csproj, and OfflineStorageService opens a
// fresh SqliteConnection per call against a single connection string, so a plain
// temp file (no shared-cache/in-memory tricks needed) behaves exactly like the
// production file. The only change needed to make this possible was adding an
// optional dbPath parameter to the constructor (production/DI behavior for
// `new OfflineStorageService()` is unchanged; see OfflineStorageService.cs).
public class OfflineStorageServiceTest : IDisposable
{
    private readonly string _dbPath;
    private readonly OfflineStorageService _service;

    public OfflineStorageServiceTest()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"vvcash-sellers-test-{Guid.NewGuid()}.db");
        _service = new OfflineStorageService(_dbPath);
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections by connection string, so the native
        // file handle can outlive a disposed SqliteConnection — deleting the file
        // without clearing the pool first fails silently on Windows (file in use).
        SqliteConnection.ClearAllPools();

        // SQLite may also leave -wal/-shm files alongside the main db; sweep all three.
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _dbPath + "-journal" })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort cleanup */ }
        }
    }

    private static SellerInfo MakeSeller(
        string id = "seller-1",
        string firstName = "Anna",
        string lastName = "Ivanova",
        string pinHash = "",
        bool canSell = true,
        bool canRefund = false,
        bool canCloseShift = false,
        decimal maxDiscount = 0m) => new()
    {
        Id = id,
        FirstName = firstName,
        LastName = lastName,
        PinHash = pinHash,
        CanSell = canSell,
        CanRefund = canRefund,
        CanCloseShift = canCloseShift,
        MaxDiscount = maxDiscount
    };

    [Fact]
    public async Task SaveAndGetSellers_EmptyPinHashRoundTripsAsEmptyStringNotNull()
    {
        await _service.InitializeAsync();
        await _service.SaveSellersAsync(new[] { MakeSeller(pinHash: "") });

        var result = (await _service.GetSellersAsync()).Single();

        // Must be "" and HasPin must be false — not null, which would also make
        // HasPin false but for the wrong reason and could NRE other callers.
        Assert.Equal(string.Empty, result.PinHash);
        Assert.False(result.HasPin);
    }

    [Fact]
    public async Task SaveAndGetSellers_DecimalMaxDiscountRoundTripsExactlyThroughRealColumn()
    {
        await _service.InitializeAsync();
        await _service.SaveSellersAsync(new[] { MakeSeller(maxDiscount: 12.5m) });

        var result = (await _service.GetSellersAsync()).Single();

        Assert.Equal(12.5m, result.MaxDiscount);
    }

    [Fact]
    public async Task SaveAndGetSellers_ZeroMaxDiscountIsPreservedNotCoercedOrLost()
    {
        // 0 means "no cap configured", not "no discounts allowed" (see task context) —
        // it must survive the round trip as a real 0, not e.g. become null or NaN.
        await _service.InitializeAsync();
        await _service.SaveSellersAsync(new[] { MakeSeller(maxDiscount: 0m) });

        var result = (await _service.GetSellersAsync()).Single();

        Assert.Equal(0m, result.MaxDiscount);
    }

    [Fact]
    public async Task SaveAndGetSellers_BooleanCapabilityFlagsRoundTripIndependently()
    {
        await _service.InitializeAsync();
        var seller = MakeSeller(canSell: true, canRefund: true, canCloseShift: false, pinHash: "pbkdf2_sha256$100000$c2FsdA==$aGFzaA==");
        await _service.SaveSellersAsync(new[] { seller });

        var result = (await _service.GetSellersAsync()).Single();

        Assert.True(result.CanSell);
        Assert.True(result.CanRefund);
        Assert.False(result.CanCloseShift);
        Assert.Equal(seller.PinHash, result.PinHash);
        Assert.True(result.HasPin);
    }

    [Fact]
    public async Task SaveSellersAsync_SecondSaveReplacesRosterRatherThanAccumulating()
    {
        await _service.InitializeAsync();
        await _service.SaveSellersAsync(new[]
        {
            MakeSeller(id: "seller-1", firstName: "Anna"),
            MakeSeller(id: "seller-2", firstName: "Boris")
        });

        // Simulates a roster refresh where seller-2 left and seller-3 joined.
        await _service.SaveSellersAsync(new[] { MakeSeller(id: "seller-3", firstName: "Carla") });

        var result = (await _service.GetSellersAsync()).ToList();

        var single = Assert.Single(result);
        Assert.Equal("seller-3", single.Id);
        Assert.Equal("Carla", single.FirstName);
    }

    [Fact]
    public async Task SaveSellersAsync_PersistsMultipleDistinctSellers()
    {
        await _service.InitializeAsync();
        await _service.SaveSellersAsync(new[]
        {
            MakeSeller(id: "seller-1", firstName: "Anna"),
            MakeSeller(id: "seller-2", firstName: "Boris")
        });

        var result = (await _service.GetSellersAsync()).ToList();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.Id == "seller-1" && s.FirstName == "Anna");
        Assert.Contains(result, s => s.Id == "seller-2" && s.FirstName == "Boris");
    }

    [Fact]
    public async Task SaveAndGetCashFeatures_RoundTripsFlags()
    {
        await _service.InitializeAsync();
        var features = new CashFeatures
        {
            Flags = new Dictionary<string, bool>
            {
                [CashFeatureCodes.Returns] = false,
                [CashFeatureCodes.ParkedSales] = true
            }
        };
        await _service.SaveCashFeaturesAsync(features);

        var result = await _service.GetCashFeaturesAsync();

        Assert.False(result.IsEnabled(CashFeatureCodes.Returns));
        Assert.True(result.IsEnabled(CashFeatureCodes.ParkedSales));
        // Never stored for this register -> reads as enabled, same as an unknown code always does.
        Assert.True(result.IsEnabled(CashFeatureCodes.MixedPayment));
    }

    [Fact]
    public async Task GetCashFeaturesAsync_NothingCached_ReturnsAllEnabledDefault()
    {
        // A register that has never synced must be fully functional, not locked down.
        await _service.InitializeAsync();

        var result = await _service.GetCashFeaturesAsync();

        Assert.Empty(result.Flags);
        Assert.True(result.IsEnabled(CashFeatureCodes.Returns));
        Assert.True(result.IsEnabled(CashFeatureCodes.ParkedSales));
    }

    [Fact]
    public async Task GetCashFeaturesAsync_CorruptCache_ReturnsAllEnabledDefaultRatherThanThrowing()
    {
        // A register must still open when its cache is damaged, e.g. by a crash mid-write.
        await _service.InitializeAsync();

        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO Settings (Key, Value) VALUES ('CashFeatures', 'not valid json')
                ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;
            ";
            await command.ExecuteNonQueryAsync();
        }

        var result = await _service.GetCashFeaturesAsync();

        Assert.Empty(result.Flags);
        Assert.True(result.IsEnabled(CashFeatureCodes.Returns));
    }
}
