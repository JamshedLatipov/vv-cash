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
// <-> TEXT conversion, NULL vs. empty-string handling, the delete-then-insert
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
    public async Task SaveAndGetSellers_DecimalMaxDiscountRoundTripsExactly()
    {
        await _service.InitializeAsync();
        // 12.5 on purpose: an ordinary discount, and one a REAL column returns unchanged.
        // This is the common-case smoke check. It says nothing about the storage class,
        // and is not meant to — that is the precision test directly below.
        await _service.SaveSellersAsync(new[] { MakeSeller(maxDiscount: 12.5m) });

        var result = (await _service.GetSellersAsync()).Single();

        Assert.Equal(12.5m, result.MaxDiscount);
    }

    /// <summary>Covers the seller write path's TEXT binding, which nothing else did. The
    /// test above uses 12.5, which a REAL column hands back unchanged, so it stayed green
    /// whether MaxDiscount was TEXT or REAL. This value does not survive REAL.
    ///
    /// Same caveat as the product precision test: what REAL reads it back as is not quoted
    /// here, because it varies with the read path. Revert MaxDiscount to REAL — in the
    /// schema block and in Sellers_new — and watch this go red.</summary>
    [Fact]
    public async Task SaveAndGetSellers_MaxDiscountThatDoesNotSurviveDouble_RoundTripsExactly()
    {
        await _service.InitializeAsync();
        await _service.SaveSellersAsync(new[] { MakeSeller(maxDiscount: 33.333333333333333m) });

        var result = (await _service.GetSellersAsync()).Single();

        Assert.Equal(33.333333333333333m, result.MaxDiscount);
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

    /// <summary>The reconciliation contract in one test: products the walk did not see
    /// at all are deleted, products it saw keep their row and gain a quantity, and a
    /// zero quantity is a value like any other — not a reason to delete.</summary>
    [Fact]
    public async Task ApplyRemainsAsync_DeletesUnseenProductsAndStampsQuantities()
    {
        await _service.InitializeAsync();
        await _service.SaveProductsAsync(new[]
        {
            new Product { Id = "in-stock", Name = "Есть", Price = 10m },
            new Product { Id = "zero", Name = "Ноль", Price = 20m },
            new Product { Id = "withdrawn", Name = "Снят", Price = 30m },
        });

        await _service.ApplyRemainsAsync(new Dictionary<string, decimal>
        {
            ["in-stock"] = 7.5m,
            ["zero"] = 0m,
        });

        var all = (await _service.GetAllProductsAsync()).ToList();

        // Asserted first, on the whole set: a delete that took the wrong row would
        // otherwise pass DoesNotContain and only surface two lines later as a bare
        // "sequence contains no matching element" from Single(), with no id in sight.
        Assert.Equal(new[] { "in-stock", "zero" }, all.Select(p => p.Id).OrderBy(x => x));
        Assert.DoesNotContain(all, p => p.Id == "withdrawn");
        Assert.Equal(7.5m, all.Single(p => p.Id == "in-stock").StockQuantity);
        Assert.Equal(0m, all.Single(p => p.Id == "zero").StockQuantity);
        Assert.True(all.Single(p => p.Id == "zero").IsOutOfStock);
        Assert.False(all.Single(p => p.Id == "in-stock").IsOutOfStock);

        // A second call on the same service exercises the pooled-connection path: SQLite
        // temp tables live for the underlying native connection, which Microsoft.Data.Sqlite
        // pools by connection string, so a naive implementation could let RemainSeen leak
        // rows from this call into the next. DELETE FROM RemainSeen at the top of
        // ApplyRemainsAsync is what stops "zero" surviving a reconciliation that no
        // longer mentions it.
        await _service.ApplyRemainsAsync(new Dictionary<string, decimal>
        {
            ["in-stock"] = 2m,
        });

        var afterSecondCall = (await _service.GetAllProductsAsync()).ToList();

        Assert.Equal(new[] { "in-stock" }, afterSecondCall.Select(p => p.Id).OrderBy(x => x));
        Assert.Equal(2m, afterSecondCall.Single(p => p.Id == "in-stock").StockQuantity);
    }

    /// <summary>Empty map: the interface doc comment on ApplyRemainsAsync explains why
    /// this throws. Both halves matter equally in this test: throwing is not enough if
    /// the delete already ran first, so this also asserts the catalogue and its
    /// quantities are untouched, not just that the call failed.</summary>
    [Fact]
    public async Task ApplyRemainsAsync_EmptyResult_ThrowsAndLeavesTheCatalogueAlone()
    {
        await _service.InitializeAsync();
        await _service.SaveProductsAsync(new[]
        {
            new Product { Id = "p1", Name = "Один", Price = 10m },
            new Product { Id = "p2", Name = "Два", Price = 20m },
        });

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.ApplyRemainsAsync(new Dictionary<string, decimal>()));

        var all = (await _service.GetAllProductsAsync()).ToList();

        Assert.Equal(2, all.Count);
        Assert.Null(all.Single(p => p.Id == "p1").StockQuantity);
        Assert.Null(all.Single(p => p.Id == "p2").StockQuantity);
    }

    /// <summary>Oversold stock: cloudmarket-server records an allowed oversell as a
    /// negative remain rather than flooring it at zero (remains.go), so this is the
    /// worst case the feature exists to catch, not an edge case — it must not read as
    /// "in stock".</summary>
    [Fact]
    public async Task ApplyRemainsAsync_NegativeQuantity_IsOutOfStockAndRoundTrips()
    {
        await _service.InitializeAsync();
        await _service.SaveProductsAsync(new[]
        {
            new Product { Id = "oversold", Name = "Перепродан", Price = 10m },
        });

        await _service.ApplyRemainsAsync(new Dictionary<string, decimal>
        {
            ["oversold"] = -3m,
        });

        var product = Assert.Single(await _service.GetAllProductsAsync());

        Assert.Equal(-3m, product.StockQuantity);
        Assert.True(product.IsOutOfStock);
    }

    /// <summary>DELETE FROM RemainSeen, at the top of ApplyRemainsAsync, guards against
    /// a row this call did not write: Microsoft.Data.Sqlite pools connections by
    /// connection string and a TEMP TABLE lives for the underlying native connection, so
    /// a row stranded there by anything earlier on that same pooled connection — most
    /// concretely, a prior call that threw before reaching its own DROP TABLE — would
    /// otherwise still be present. Planted directly here rather than provoked via a real
    /// failure, because what matters is that ApplyRemainsAsync does not trust RemainSeen
    /// content it did not itself write this call, regardless of how it got there.
    /// "withdrawn" is the id the current walk excludes — the guard's absence has to
    /// leave a real product wrongly alive, not just an orphan row nothing reads.</summary>
    [Fact]
    public async Task ApplyRemainsAsync_StaleRemainSeenRow_DoesNotResurrectAWithdrawnProduct()
    {
        await _service.InitializeAsync();
        await _service.SaveProductsAsync(new[]
        {
            new Product { Id = "in-stock", Name = "Есть", Price = 10m },
            new Product { Id = "withdrawn", Name = "Снят", Price = 20m },
        });

        // Plants a row for "withdrawn" on whatever native connection the pool hands back
        // next — the same one ApplyRemainsAsync below will get, since this service's
        // connection string never changes and nothing else touches the pool in between.
        var connectionString = $"Data Source={_dbPath}";
        using (var stale = new SqliteConnection(connectionString))
        {
            await stale.OpenAsync();
            using var cmd = stale.CreateCommand();
            cmd.CommandText = "CREATE TEMP TABLE IF NOT EXISTS RemainSeen (Id TEXT PRIMARY KEY NOT NULL, Qty TEXT NOT NULL);"
                             + "INSERT OR REPLACE INTO RemainSeen (Id, Qty) VALUES ('withdrawn', '1');";
            await cmd.ExecuteNonQueryAsync();
        }

        // This walk's real result does not mention "withdrawn" at all.
        await _service.ApplyRemainsAsync(new Dictionary<string, decimal>
        {
            ["in-stock"] = 5m,
        });

        var all = (await _service.GetAllProductsAsync()).ToList();

        Assert.Equal(new[] { "in-stock" }, all.Select(p => p.Id).OrderBy(x => x));
        Assert.Equal(5m, all.Single().StockQuantity);
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

    /// <summary>Builds a database in the pre-migration shape and checks that
    /// InitializeAsync rebuilds Products: declared types become TEXT, the row survives,
    /// the indices come back, and the new StockQuantity column is there. The other two
    /// rebuilds run on the same seeded database and are asserted separately below.
    ///
    /// The indices matter and are easy to lose: they are created in the schema block
    /// that runs earlier in the same InitializeAsync, and DROP TABLE takes them with
    /// the table.</summary>
    [Fact]
    public async Task InitializeAsync_UpgradingFromRealColumns_RebuildsAsTextAndKeepsRows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vvcash-migrate-{Guid.NewGuid()}.db");
        try
        {
            await SeedPreMigrationDatabaseAsync(dbPath);

            await new OfflineStorageService(dbPath).InitializeAsync();

            using var check = new SqliteConnection($"Data Source={dbPath}");
            await check.OpenAsync();

            Assert.Equal("TEXT", await DeclaredTypeAsync(check, "Products", "Price"));
            Assert.Equal("TEXT", await DeclaredTypeAsync(check, "Products", "OriginalPrice"));
            Assert.Equal("TEXT", await DeclaredTypeAsync(check, "Products", "DiscountPercent"));
            Assert.Equal("TEXT", await DeclaredTypeAsync(check, "Products", "UnitFactor"));
            Assert.Equal("TEXT", await DeclaredTypeAsync(check, "Products", "StockQuantity"));

            using (var cmd = check.CreateCommand())
            {
                cmd.CommandText = "SELECT Name, Price, UnitFactor FROM Products WHERE Id = 'p-1';";
                using var rd = await cmd.ExecuteReaderAsync();
                Assert.True(await rd.ReadAsync());
                Assert.Equal("Товар", rd.GetString(0));
                Assert.Equal(19.99m, rd.GetDecimal(1));
                Assert.Equal(2.5m, rd.GetDecimal(2));
            }

            using (var cmd = check.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='Products';";
                var found = new List<string>();
                using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync()) found.Add(rd.GetString(0));
                Assert.Contains("IDX_Products_Category", found);
                Assert.Contains("IDX_Products_Barcode", found);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        }
    }

    private static async Task<string> DeclaredTypeAsync(SqliteConnection connection, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT type FROM pragma_table_info('{table}') WHERE name = $c;";
        cmd.Parameters.AddWithValue("$c", column);
        return (await cmd.ExecuteScalarAsync()) as string ?? string.Empty;
    }

    /// <summary>A parked sale's Payload is the only record of what a cashier is holding:
    /// no sync restores it, so the rebuild has to hand it back byte for byte. Deliberately
    /// awkward content — Cyrillic, quotes, a percent sign — so a rebuild that mangles
    /// encoding or quoting shows up here and not on a shop floor.</summary>
    private const string ParkedSalePayload =
        @"{""items"":[{""name"":""Товар «А»"",""qty"":2.5}],""note"":""скидка 50%""}";

    /// <summary>Nothing else exercised SaveParkedSaleAsync into GetParkedSaleAsync. Products
    /// and Sellers come back from a sync if a write path loses them, and UnsyncedDocuments is
    /// covered separately — a parked sale is a receipt a cashier is holding, and this round
    /// trip is the only thing carrying it.
    ///
    /// Total is a value a REAL column would round, which makes this test do double duty:
    /// SaveParkedSaleAsync binds through AddWithValue rather than the explicit SqliteType.Text
    /// that Products and Sellers use, and AddWithValue binding a decimal as text is measured
    /// behaviour rather than a declared intent. If that ever stops being true, this assertion
    /// is what says so.</summary>
    [Fact]
    public async Task SaveAndGetParkedSale_RoundTripsEveryFieldExactly()
    {
        await _service.InitializeAsync();

        // Sub-millisecond ticks on purpose: CreatedAt is stored with ToString("o") and read
        // back with RoundtripKind, so anything coarser would silently pass on a narrower format.
        var createdAt = new DateTime(2026, 8, 23, 14, 32, 18, DateTimeKind.Utc).AddTicks(1234567);

        await _service.SaveParkedSaleAsync(new ParkedSale
        {
            Id = "parked-1",
            Label = "Касса 1",
            CustomerName = "Пётр «Кузнецов»",
            Total = 12345678901234.56m,
            ItemCount = 2.5m,
            CreatedAt = createdAt,
            Payload = ParkedSalePayload,
        });

        var loaded = await _service.GetParkedSaleAsync("parked-1");

        Assert.NotNull(loaded);
        Assert.Equal("parked-1", loaded!.Id);
        Assert.Equal("Касса 1", loaded.Label);
        Assert.Equal("Пётр «Кузнецов»", loaded.CustomerName);
        Assert.Equal(12345678901234.56m, loaded.Total);
        Assert.Equal(2.5m, loaded.ItemCount);
        Assert.Equal(createdAt, loaded.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, loaded.CreatedAt.Kind);
        Assert.Equal(ParkedSalePayload, loaded.Payload);
    }

    /// <summary>Creates all three money-carrying tables in the shape that shipped before
    /// this migration, one row each.
    ///
    /// All three and not just Products, because each rebuild sits behind its own probe and
    /// CREATE TABLE IF NOT EXISTS creates whatever is missing already declared TEXT. Seed
    /// Products alone and the ParkedSales and Sellers branches are not merely untested —
    /// they never execute at all.</summary>
    private static async Task SeedPreMigrationDatabaseAsync(string dbPath)
    {
        using var seed = new SqliteConnection($"Data Source={dbPath}");
        await seed.OpenAsync();

        using (var cmd = seed.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE Products (
                    Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Sku TEXT, Category TEXT,
                    Price REAL NOT NULL, OriginalPrice REAL, DiscountPercent REAL,
                    ImagePath TEXT, Barcode TEXT, Tags TEXT,
                    UnitId TEXT, UnitCode TEXT, UnitShortName TEXT, UnitFactor REAL,
                    IsDivisible INTEGER, SellInSecondaryUnit INTEGER, SearchText TEXT
                );
                CREATE TABLE ParkedSales (
                    Id TEXT PRIMARY KEY, Label TEXT, CustomerName TEXT,
                    Total REAL NOT NULL, ItemCount REAL NOT NULL,
                    CreatedAt TEXT NOT NULL, Payload TEXT NOT NULL
                );
                CREATE TABLE Sellers (
                    Id TEXT PRIMARY KEY, FirstName TEXT NOT NULL, LastName TEXT,
                    PinHash TEXT, CanSell INTEGER NOT NULL DEFAULT 1,
                    CanRefund INTEGER NOT NULL DEFAULT 0,
                    CanCloseShift INTEGER NOT NULL DEFAULT 0,
                    MaxDiscount REAL NOT NULL DEFAULT 0
                );
                INSERT INTO Products (Id, Name, Price, UnitFactor, SearchText)
                VALUES ('p-1', 'Товар', 19.99, 2.5, 'товар');
                INSERT INTO Sellers (Id, FirstName, LastName, PinHash, CanSell, CanRefund, CanCloseShift, MaxDiscount)
                VALUES ('s-1', 'Анна', 'Иванова', 'pin-hash', 1, 0, 1, 12.5);
            ";
            await cmd.ExecuteNonQueryAsync();
        }

        // Parameterised rather than inlined: the payload carries quotes on purpose, and
        // this must not turn into a test of SQL string-escaping.
        using (var parked = seed.CreateCommand())
        {
            parked.CommandText = @"
                INSERT INTO ParkedSales (Id, Label, CustomerName, Total, ItemCount, CreatedAt, Payload)
                VALUES ('k-1', 'Касса 1', 'Пётр', 1234.56, 2.5, '2026-08-23T10:00:00Z', $Payload);
            ";
            parked.Parameters.AddWithValue("$Payload", ParkedSalePayload);
            await parked.ExecuteNonQueryAsync();
        }
    }

    /// <summary>ParkedSales is the one table here that no sync can rebuild. Products and
    /// Sellers are caches: lose a row and the next sync puts it back. A parked sale is a
    /// receipt a cashier is holding and Payload is the only copy of it anywhere, so a
    /// rebuild that drops or mangles a row destroys it for good.</summary>
    [Fact]
    public async Task InitializeAsync_UpgradingFromRealColumns_KeepsParkedSaleRowsIntact()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vvcash-migrate-parked-{Guid.NewGuid()}.db");
        try
        {
            await SeedPreMigrationDatabaseAsync(dbPath);

            await new OfflineStorageService(dbPath).InitializeAsync();

            using var check = new SqliteConnection($"Data Source={dbPath}");
            await check.OpenAsync();

            Assert.Equal("TEXT", await DeclaredTypeAsync(check, "ParkedSales", "Total"));
            Assert.Equal("TEXT", await DeclaredTypeAsync(check, "ParkedSales", "ItemCount"));

            using var cmd = check.CreateCommand();
            cmd.CommandText =
                "SELECT Label, CustomerName, Total, ItemCount, CreatedAt, Payload "
                + "FROM ParkedSales WHERE Id = 'k-1';";
            using var rd = await cmd.ExecuteReaderAsync();
            Assert.True(await rd.ReadAsync());
            Assert.Equal("Касса 1", rd.GetString(0));
            Assert.Equal("Пётр", rd.GetString(1));
            Assert.Equal(1234.56m, rd.GetDecimal(2));
            Assert.Equal(2.5m, rd.GetDecimal(3));
            Assert.Equal("2026-08-23T10:00:00Z", rd.GetString(4));
            Assert.Equal(ParkedSalePayload, rd.GetString(5));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        }
    }

    /// <summary>The Sellers rebuild redeclares MaxDiscount and restates its DEFAULT as a
    /// quoted '0'. The capability flags ride along in the same copy, and they are what the
    /// register gates refunds and shift-close on until the next sync — so a seller who
    /// could close a shift before the upgrade must still be able to after it.</summary>
    [Fact]
    public async Task InitializeAsync_UpgradingFromRealColumns_KeepsSellerRowsIntact()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vvcash-migrate-sellers-{Guid.NewGuid()}.db");
        try
        {
            await SeedPreMigrationDatabaseAsync(dbPath);

            await new OfflineStorageService(dbPath).InitializeAsync();

            using var check = new SqliteConnection($"Data Source={dbPath}");
            await check.OpenAsync();

            Assert.Equal("TEXT", await DeclaredTypeAsync(check, "Sellers", "MaxDiscount"));

            using var cmd = check.CreateCommand();
            cmd.CommandText =
                "SELECT FirstName, LastName, PinHash, CanSell, CanRefund, CanCloseShift, MaxDiscount "
                + "FROM Sellers WHERE Id = 's-1';";
            using var rd = await cmd.ExecuteReaderAsync();
            Assert.True(await rd.ReadAsync());
            Assert.Equal("Анна", rd.GetString(0));
            Assert.Equal("Иванова", rd.GetString(1));
            Assert.Equal("pin-hash", rd.GetString(2));
            Assert.True(rd.GetBoolean(3));
            Assert.False(rd.GetBoolean(4));
            Assert.True(rd.GetBoolean(5));
            Assert.Equal(12.5m, rd.GetDecimal(6));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        }
    }

    /// <summary>The values matter more than the assertions. Ordinary prices survive a REAL
    /// column intact — 19.99 and 1234.56 both round-trip through a double without loss — so
    /// a test built on one of those would have been green before this migration as well as
    /// after it, and would have guarded nothing. These two do not survive REAL, which is the
    /// entire reason they are the values used here.
    ///
    /// What REAL reads them back as is deliberately not quoted. That figure is not a stable
    /// fact: it depends on the read path, because SQLite's own text rendering keeps fifteen
    /// significant digits while converting the double straight to decimal keeps about
    /// seventeen. Three people measured this and got three different answers, each correct
    /// for how they measured. To confirm this test still has teeth, revert a column to REAL
    /// and watch it go red — do not compare against a remembered number.</summary>
    [Fact]
    public async Task SaveProductsAsync_ValuesThatDoNotSurviveDouble_RoundTripExactly()
    {
        await _service.InitializeAsync();
        await _service.SaveProductsAsync(new[]
        {
            new Product
            {
                Id = "p-precise",
                Name = "Точность",
                Price = 12345678901234.56m,
                UnitFactor = 1.000000000000001m,
            }
        });

        var loaded = (await _service.GetAllProductsAsync()).Single(p => p.Id == "p-precise");

        Assert.Equal(12345678901234.56m, loaded.Price);
        Assert.Equal(1.000000000000001m, loaded.UnitFactor);
    }
}
