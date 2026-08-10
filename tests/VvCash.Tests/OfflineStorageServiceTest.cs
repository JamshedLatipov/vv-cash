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

    [Fact]
    public async Task SaveProductsAsync_RoundTripsTheSecondaryUnit()
    {
        await _service.InitializeAsync();
        await _service.SaveProductsAsync(new[]
        {
            new Product
            {
                Id = "p1", Name = "Плитка", Price = 100m,
                UnitId = "u-1", UnitCode = "m2", UnitShortName = "м²",
                UnitFactor = 0.24m, IsDivisible = false, SellInSecondaryUnit = true,
            },
        });

        var product = Assert.Single(await _service.GetAllProductsAsync());
        Assert.Equal("u-1", product.UnitId);
        Assert.Equal("m2", product.UnitCode);
        Assert.Equal("м²", product.UnitShortName);
        Assert.Equal(0.24m, product.UnitFactor);
        Assert.False(product.IsDivisible);
        Assert.True(product.SellInSecondaryUnit);
    }

    [Fact]
    public async Task SaveProductsAsync_RoundTripsAPieceOnlyProduct()
    {
        await _service.InitializeAsync();
        await _service.SaveProductsAsync(new[]
        {
            new Product { Id = "p2", Name = "Товар", Price = 10m },
        });

        var product = Assert.Single(await _service.GetAllProductsAsync());
        Assert.Equal(string.Empty, product.UnitId);
        Assert.Equal(0m, product.UnitFactor);
        Assert.False(product.HasSecondaryUnit);
    }

    // ---------------------------------------------------------------------------------
    // Search. Every keystroke in the POS search box used to load the whole catalog out
    // of SQLite and filter it in memory — a full table scan plus one materialised
    // Product per row, per character typed.
    // ---------------------------------------------------------------------------------

    private static Product Searchable(string id, string name, string sku = "", string barcode = "") =>
        new() { Id = id, Name = name, Sku = sku, Barcode = barcode, Price = 10m };

    [Fact]
    public async Task SearchProductsAsync_MatchesNameSkuAndBarcode()
    {
        await _service.InitializeAsync();
        await _service.SaveProductsAsync(new[]
        {
            Searchable("p1", "Плитка настенная", sku: "TILE-1", barcode: "4600001"),
            Searchable("p2", "Краска白", sku: "PAINT-9", barcode: "4600002"),
        });

        Assert.Equal("p1", Assert.Single(await _service.SearchProductsAsync("настен")).Id);
        Assert.Equal("p2", Assert.Single(await _service.SearchProductsAsync("PAINT")).Id);
        Assert.Equal("p1", Assert.Single(await _service.SearchProductsAsync("4600001")).Id);
    }

    [Fact]
    public async Task SearchProductsAsync_IsCaseInsensitiveForCyrillic()
    {
        // The reason the match column is lowercased in C# rather than by SQLite's own
        // lower(): SQLite's built-in one only folds ASCII, so "ПЛИТКА" would never find
        // "Плитка" — which is most of this catalog.
        await _service.InitializeAsync();
        await _service.SaveProductsAsync(new[] { Searchable("p1", "Плитка настенная") });

        Assert.Equal("p1", Assert.Single(await _service.SearchProductsAsync("ПЛИТКА")).Id);
        Assert.Equal("p1", Assert.Single(await _service.SearchProductsAsync("плитка")).Id);
    }

    [Fact]
    public async Task SearchProductsAsync_TreatsWildcardsAsOrdinaryCharacters()
    {
        // '%' and '_' are LIKE syntax. A cashier typing either must not match everything.
        await _service.InitializeAsync();
        await _service.SaveProductsAsync(new[]
        {
            Searchable("p1", "Плитка"),
            Searchable("p2", "Скидка 50% декабрь"),
        });

        Assert.Equal("p2", Assert.Single(await _service.SearchProductsAsync("50%")).Id);
        Assert.Empty(await _service.SearchProductsAsync("_"));
    }

    [Fact]
    public async Task SearchProductsAsync_FindsRowsWrittenBeforeTheSearchColumnExisted()
    {
        // A register upgrading in the middle of the day has a full Products table and no
        // match column yet. If the migration only added the column, search would come
        // back empty until the next full catalog sync — which is up to SyncIntervalMinutes
        // away, and needs a connection the register may not have.
        await _service.InitializeAsync();
        await _service.SaveProductsAsync(new[] { Searchable("p1", "Плитка настенная") });

        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Products SET SearchText = NULL";
            await command.ExecuteNonQueryAsync();
        }

        var reopened = new OfflineStorageService(_dbPath);
        await reopened.InitializeAsync();

        Assert.Equal("p1", Assert.Single(await reopened.SearchProductsAsync("настен")).Id);
    }

    [Fact]
    public async Task SearchProductsAsync_BlankQueryReturnsNothing()
    {
        await _service.InitializeAsync();
        await _service.SaveProductsAsync(new[] { Searchable("p1", "Плитка") });

        Assert.Empty(await _service.SearchProductsAsync("   "));
    }
}
