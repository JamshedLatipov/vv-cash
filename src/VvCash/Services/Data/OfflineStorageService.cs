using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Data.Sqlite;
using VvCash.Models;

namespace VvCash.Services.Data;

public class OfflineStorageService : IOfflineStorageService
{
    private readonly string _connectionString;
    private bool _isInitialized = false;

    /// <summary>Serialises InitializeAsync. The fast path still reads _isInitialized
    /// without the lock — that read is only ever false-negative, and a false negative
    /// costs one uncontended WaitAsync, not a second initialisation: the flag is
    /// re-checked under the lock.
    ///
    /// Needed today, not only in anticipation of a heavier InitializeAsync: this service
    /// is a singleton and PosViewModel is transient, so a logout→login cycle starts a
    /// second InitializeAsync on top of a first one that has not finished — the call is
    /// fire-and-forget from the view model's constructor (PosViewModel.cs:874).</summary>
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>Creates the service against the standard per-user database file.
    /// Pass <paramref name="dbPath"/> to point at a different file (e.g. a temp file in tests);
    /// left null/empty, DI and production code get the usual LocalApplicationData path unchanged.</summary>
    public OfflineStorageService(string? dbPath = null)
    {
        if (string.IsNullOrEmpty(dbPath))
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appDir = Path.Combine(appDataPath, "VvCash");
            Directory.CreateDirectory(appDir);
            dbPath = Path.Combine(appDir, "offline_data.db");
        }
        _connectionString = $"Data Source={dbPath}";
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        await _initLock.WaitAsync();
        try
        {
            if (_isInitialized) return;

            await InitializeCoreAsync();

            _isInitialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>Everything InitializeAsync does once it has decided it is the one doing
    /// it: schema, additive column migrations, the REAL-to-TEXT table rebuilds, and the
    /// SearchText backfill. Split out so the guard above stays readable — the same shape
    /// UpdateService.DownloadAsync uses around DownloadCoreAsync.</summary>
    private async Task InitializeCoreAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();

        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT
            );

            -- RejectedAt/RejectedReason are NULL for a document still waiting its turn.
            -- A non-null RejectedAt means the server answered, on the merits, that it
            -- will not take this document: it leaves the retry rotation but stays on
            -- disk, because it is still the only record of what the register booked.
            CREATE TABLE IF NOT EXISTS UnsyncedDocuments (
                Hash TEXT PRIMARY KEY,
                Payload TEXT,
                RejectedAt TEXT,
                RejectedReason TEXT
            );

            CREATE TABLE IF NOT EXISTS Categories (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                IsQuickAccess INTEGER NOT NULL DEFAULT 0,
                ImageUrl TEXT,
                ParentId TEXT
            );

            CREATE TABLE IF NOT EXISTS Products (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Sku TEXT,
                Category TEXT,
                -- TEXT, not REAL: REAL affinity converts what is written to a float,
                -- and these are money. Microsoft.Data.Sqlite writes decimal to TEXT
                -- culture-invariantly (measured under ru-RU), and GetDecimal reads it
                -- back exactly. See the batch C spec for the measurement.
                Price TEXT NOT NULL,
                OriginalPrice TEXT,
                DiscountPercent TEXT,
                ImagePath TEXT,
                Barcode TEXT,
                Tags TEXT,
                UnitId TEXT,
                UnitCode TEXT,
                UnitShortName TEXT,
                UnitFactor TEXT,
                IsDivisible INTEGER,
                SellInSecondaryUnit INTEGER,
                -- Name, Sku and Barcode joined and lowercased, for the POS search box.
                -- Lowercased in C# rather than by SQLite's own lower(), which folds ASCII
                -- only: an all-caps Cyrillic query would never match its own product, and
                -- that is most of this catalog. See SearchTextOf/SearchProductsAsync below.
                SearchText TEXT,
                -- Stock for this register's warehouse as of the last complete
                -- reconciliation walk. NULL means the walk has never completed, and the
                -- register behaves exactly as it did before the walk existed.
                StockQuantity TEXT
            );

            -- Auto-applied promotions, stored as the raw server payload: the rules
            -- and targets are nested lists that would need two more tables and a
            -- join to reassemble, and nothing here ever queries into them.
            CREATE TABLE IF NOT EXISTS Promotions (
                Id TEXT PRIMARY KEY,
                Payload TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ParkedSales (
                Id TEXT PRIMARY KEY,
                Label TEXT,
                CustomerName TEXT,
                Total TEXT NOT NULL,
                -- TEXT, and not INTEGER, for the same reason it was never INTEGER: a
                -- weighted line contributes a fraction of a unit. TEXT rather than REAL
                -- because a fraction is exactly what a float rounds.
                ItemCount TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                Payload TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Sellers (
                Id TEXT PRIMARY KEY,
                FirstName TEXT NOT NULL,
                LastName TEXT,
                PinHash TEXT,
                CanSell INTEGER NOT NULL DEFAULT 1,
                CanRefund INTEGER NOT NULL DEFAULT 0,
                CanCloseShift INTEGER NOT NULL DEFAULT 0,
                MaxDiscount TEXT NOT NULL DEFAULT '0'
            );

            -- Create indices for performance
            CREATE INDEX IF NOT EXISTS IDX_Products_Category ON Products(Category);
            CREATE INDEX IF NOT EXISTS IDX_Products_Barcode ON Products(Barcode);
        ";

        await command.ExecuteNonQueryAsync();

        // Ensure LastSyncVersion setting exists
        command.CommandText = "INSERT OR IGNORE INTO Settings (Key, Value) VALUES ('LastSyncVersion', '0');";
        await command.ExecuteNonQueryAsync();

        // Migrations for a database created before a column existed. Every one of these
        // is expected to fail on a register that already has the column, and expected to
        // succeed exactly once on one that does not.
        await AddColumnIfMissingAsync(command, "ALTER TABLE Categories ADD COLUMN ImageUrl TEXT;");
        await AddColumnIfMissingAsync(command, "ALTER TABLE Categories ADD COLUMN ParentId TEXT;");
        await AddColumnIfMissingAsync(command, "ALTER TABLE Products ADD COLUMN Tags TEXT;");

        // Migration: the rejected-document columns. A register upgrading with documents
        // already queued keeps them — they read as NULL, i.e. still awaiting a retry,
        // which is exactly what they are.
        foreach (var alter in new[]
        {
            "ALTER TABLE UnsyncedDocuments ADD COLUMN RejectedAt TEXT;",
            "ALTER TABLE UnsyncedDocuments ADD COLUMN RejectedReason TEXT;",
        })
        {
            try
            {
                command.CommandText = alter;
                await command.ExecuteNonQueryAsync();
            }
            catch { /* column already exists */ }
        }

        // Migration: add the secondary-unit columns to Products if upgrading
        // from an older DB. One ALTER per column, because a register may be
        // upgrading from any point in this sequence.
        foreach (var alter in new[]
        {
            "ALTER TABLE Products ADD COLUMN UnitId TEXT;",
            "ALTER TABLE Products ADD COLUMN UnitCode TEXT;",
            "ALTER TABLE Products ADD COLUMN UnitShortName TEXT;",
            // TEXT, like the schema block above declares it. A database old enough to
            // be missing this column also has Price REAL, so the rebuild below fires
            // moments later and would redeclare it TEXT anyway — this just stops the
            // line reading as a leftover REAL that somebody should "fix" back.
            "ALTER TABLE Products ADD COLUMN UnitFactor TEXT;",
            "ALTER TABLE Products ADD COLUMN IsDivisible INTEGER;",
            "ALTER TABLE Products ADD COLUMN SellInSecondaryUnit INTEGER;",
            "ALTER TABLE Products ADD COLUMN SearchText TEXT;",
        })
        {
            await AddColumnIfMissingAsync(command, alter);
        }

        // Migration: money columns move from REAL to TEXT. Runs only on a register whose
        // tables were created before this landed — on a fresh database the schema block
        // above already declared TEXT and the probes below are no-ops.
        await RebuildIfRealAsync(connection, "Products", "Price", @"
            CREATE TABLE Products_new (
                Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Sku TEXT, Category TEXT,
                Price TEXT NOT NULL, OriginalPrice TEXT, DiscountPercent TEXT,
                ImagePath TEXT, Barcode TEXT, Tags TEXT,
                UnitId TEXT, UnitCode TEXT, UnitShortName TEXT, UnitFactor TEXT,
                IsDivisible INTEGER, SellInSecondaryUnit INTEGER, SearchText TEXT,
                StockQuantity TEXT
            );",
            // StockQuantity is deliberately absent from the copy list: the old table
            // has no such column, and NULL is the correct starting value anyway.
            "Id, Name, Sku, Category, Price, OriginalPrice, DiscountPercent, ImagePath, "
            + "Barcode, Tags, UnitId, UnitCode, UnitShortName, UnitFactor, IsDivisible, "
            + "SellInSecondaryUnit, SearchText",
            "CREATE INDEX IF NOT EXISTS IDX_Products_Category ON Products(Category);",
            "CREATE INDEX IF NOT EXISTS IDX_Products_Barcode ON Products(Barcode);");

        await RebuildIfRealAsync(connection, "ParkedSales", "Total", @"
            CREATE TABLE ParkedSales_new (
                Id TEXT PRIMARY KEY, Label TEXT, CustomerName TEXT,
                Total TEXT NOT NULL, ItemCount TEXT NOT NULL,
                CreatedAt TEXT NOT NULL, Payload TEXT NOT NULL
            );",
            "Id, Label, CustomerName, Total, ItemCount, CreatedAt, Payload");

        await RebuildIfRealAsync(connection, "Sellers", "MaxDiscount", @"
            CREATE TABLE Sellers_new (
                Id TEXT PRIMARY KEY, FirstName TEXT NOT NULL, LastName TEXT,
                PinHash TEXT, CanSell INTEGER NOT NULL DEFAULT 1,
                CanRefund INTEGER NOT NULL DEFAULT 0,
                CanCloseShift INTEGER NOT NULL DEFAULT 0,
                MaxDiscount TEXT NOT NULL DEFAULT '0'
            );",
            "Id, FirstName, LastName, PinHash, CanSell, CanRefund, CanCloseShift, MaxDiscount");

        // Belt and braces for a database that already has TEXT columns but predates
        // StockQuantity. Cannot happen from a released build — the two shipped together —
        // but a hand-migrated register is cheap to tolerate and expensive to debug.
        await AddColumnIfMissingAsync(command, "ALTER TABLE Products ADD COLUMN StockQuantity TEXT;");

        await BackfillSearchTextAsync(connection);
    }

    /// <summary>Runs one ADD COLUMN, treating "it is already there" as the success it is.
    ///
    /// Only that. A bare catch-all here — which is what every one of these migrations
    /// used to have — swallowed a locked database, a read-only file and a corrupt schema
    /// just as quietly, and the register carried on to fail later on a read, somewhere
    /// with no connection to the actual problem.</summary>
    private static async Task AddColumnIfMissingAsync(SqliteCommand command, string alter)
    {
        try
        {
            command.CommandText = alter;
            await command.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (
            ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Already migrated. The expected outcome on every register but a fresh one.
        }
        catch (Exception ex)
        {
            // Anything else is a real problem with the database itself. Logged loudly
            // rather than thrown: a till that refuses to open helps nobody, and the
            // operation that actually needs the column will fail with its own, more
            // specific error.
            Console.WriteLine($"[OfflineStorageService] Migration failed ({alter}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The declared type of one column, or "" when the table or column is
    /// absent. Declared type, not storage class: SQLite reports what CREATE TABLE said,
    /// which is exactly the thing this migration changes.</summary>
    private static async Task<string> DeclaredTypeAsync(SqliteConnection connection, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT type FROM pragma_table_info('{table}') WHERE name = $c;";
        cmd.Parameters.AddWithValue("$c", column);
        return (await cmd.ExecuteScalarAsync()) as string ?? string.Empty;
    }

    /// <summary>Moves one table's money columns from REAL to TEXT, if they are still REAL.
    ///
    /// A failure is logged and swallowed, the same way AddColumnIfMissingAsync treats a
    /// migration it cannot run. Without that, this is the only thing in InitializeCoreAsync
    /// that can abort initialisation outright, and nobody would see it: PosViewModel starts
    /// the call as `_ = InitializeAsync();` and never observes the task, so a register that
    /// threw here would come up with no catalogue, no shift, and nothing on screen saying
    /// why.
    ///
    /// Degrading is genuinely safe. A table left declared REAL is still read through
    /// GetDecimal, which reads REAL perfectly well, so the register keeps selling at the old
    /// precision — and the probe fires again on the next launch, so the migration retries by
    /// itself. Losing precision is a bad day; a till that will not open is a closed shop.
    ///
    /// The probe reads one column and infers the shape of the rest. That holds for anything
    /// a released build can produce, because each table's money columns were declared
    /// together and move together. It does not hold for a database somebody has half
    /// repaired by hand — Products with Price already TEXT but OriginalPrice still REAL
    /// satisfies the probe and is never fixed. Accepted knowingly: the alternative is
    /// probing every money column on every launch to catch a state no release can
    /// create.</summary>
    private static async Task RebuildIfRealAsync(
        SqliteConnection connection, string table, string probeColumn,
        string createNewSql, string copiedColumns, params string[] indexes)
    {
        try
        {
            if (await DeclaredTypeAsync(connection, table, probeColumn) != "REAL") return;

            await RebuildTableAsync(connection, table, createNewSql, copiedColumns, indexes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OfflineStorageService] Rebuild failed ({table}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Rebuilds a table under a new column declaration, because SQLite has no
    /// ALTER COLUMN. Rows are copied, not dropped: a register that upgrades while
    /// offline would otherwise be left with no catalogue and nothing to sell until the
    /// next successful sync.
    ///
    /// <paramref name="indexes"/> is not optional housekeeping. Indices are created in
    /// the schema block that already ran earlier in this same InitializeAsync, and the
    /// DROP TABLE below takes them with the table.
    ///
    /// Precondition, and the helper is named generally enough that the next caller will not
    /// guess it: the table must have no foreign keys pointing at it, and no view or trigger
    /// referencing it. Microsoft.Data.Sqlite turns PRAGMA foreign_keys on by default, and
    /// under it the DROP TABLE below silently deletes the rows of any ON DELETE CASCADE
    /// child. A view or trigger over the table makes the RENAME throw instead — "error in
    /// view v: no such table: main.P". This schema has none of the three, which is the only
    /// reason the sequence below is safe as written.</summary>
    private static async Task RebuildTableAsync(
        SqliteConnection connection, string table, string createNewSql,
        string copiedColumns, params string[] indexes)
    {
        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;

        foreach (var sql in new[]
        {
            createNewSql,
            $"INSERT INTO {table}_new ({copiedColumns}) SELECT {copiedColumns} FROM {table};",
            $"DROP TABLE {table};",
            $"ALTER TABLE {table}_new RENAME TO {table};",
        }.Concat(indexes))
        {
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    /// <summary>Fills SearchText for rows written before the column existed. Adding the
    /// column alone would leave a register that upgraded mid-day with a search box that
    /// finds nothing until the next full catalog sync — which is up to SyncIntervalMinutes
    /// away and needs a connection the register may not have. Runs once: after this, every
    /// row has a value and the WHERE below matches nothing.</summary>
    private static async Task BackfillSearchTextAsync(SqliteConnection connection)
    {
        var pending = new List<(string Id, string Text)>();

        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT Id, Name, Sku, Barcode FROM Products WHERE SearchText IS NULL";
            using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                pending.Add((
                    reader.GetString(0),
                    SearchTextOf(
                        reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        reader.IsDBNull(3) ? string.Empty : reader.GetString(3))));
            }
        }

        if (pending.Count == 0) return;

        using var transaction = connection.BeginTransaction();
        using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = "UPDATE Products SET SearchText = $SearchText WHERE Id = $Id";
        var textParam = write.Parameters.Add("$SearchText", SqliteType.Text);
        var idParam = write.Parameters.Add("$Id", SqliteType.Text);

        foreach (var (id, text) in pending)
        {
            idParam.Value = id;
            textParam.Value = text;
            await write.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>The one place the match column's contents are defined, so writes and the
    /// query below can never disagree about what is being compared.</summary>
    private static string SearchTextOf(string? name, string? sku, string? barcode)
        => $"{name} {sku} {barcode}".ToLowerInvariant();

    public async Task SaveProductsAsync(IEnumerable<Product> products)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = @"
            INSERT INTO Products (Id, Name, Sku, Category, Price, OriginalPrice, DiscountPercent, ImagePath, Barcode, Tags,
                                  UnitId, UnitCode, UnitShortName, UnitFactor, IsDivisible, SellInSecondaryUnit, SearchText)
            VALUES ($Id, $Name, $Sku, $Category, $Price, $OriginalPrice, $DiscountPercent, $ImagePath, $Barcode, $Tags,
                    $UnitId, $UnitCode, $UnitShortName, $UnitFactor, $IsDivisible, $SellInSecondaryUnit, $SearchText)
            ON CONFLICT(Id) DO UPDATE SET
                SearchText=excluded.SearchText,
                Name=excluded.Name,
                Sku=excluded.Sku,
                Category=excluded.Category,
                Price=excluded.Price,
                OriginalPrice=excluded.OriginalPrice,
                DiscountPercent=excluded.DiscountPercent,
                ImagePath=excluded.ImagePath,
                Barcode=excluded.Barcode,
                Tags=excluded.Tags,
                UnitId=excluded.UnitId,
                UnitCode=excluded.UnitCode,
                UnitShortName=excluded.UnitShortName,
                UnitFactor=excluded.UnitFactor,
                IsDivisible=excluded.IsDivisible,
                SellInSecondaryUnit=excluded.SellInSecondaryUnit;
        ";

        var idParam = command.Parameters.Add("$Id", SqliteType.Text);
        var nameParam = command.Parameters.Add("$Name", SqliteType.Text);
        var skuParam = command.Parameters.Add("$Sku", SqliteType.Text);
        var categoryParam = command.Parameters.Add("$Category", SqliteType.Text);
        var priceParam = command.Parameters.Add("$Price", SqliteType.Text);
        var origPriceParam = command.Parameters.Add("$OriginalPrice", SqliteType.Text);
        var discountParam = command.Parameters.Add("$DiscountPercent", SqliteType.Text);
        var imageParam = command.Parameters.Add("$ImagePath", SqliteType.Text);
        var barcodeParam = command.Parameters.Add("$Barcode", SqliteType.Text);
        var tagsParam = command.Parameters.Add("$Tags", SqliteType.Text);
        var unitIdParam = command.Parameters.Add("$UnitId", SqliteType.Text);
        var unitCodeParam = command.Parameters.Add("$UnitCode", SqliteType.Text);
        var unitShortNameParam = command.Parameters.Add("$UnitShortName", SqliteType.Text);
        var unitFactorParam = command.Parameters.Add("$UnitFactor", SqliteType.Text);
        var isDivisibleParam = command.Parameters.Add("$IsDivisible", SqliteType.Integer);
        var sellInUnitParam = command.Parameters.Add("$SellInSecondaryUnit", SqliteType.Integer);
        var searchTextParam = command.Parameters.Add("$SearchText", SqliteType.Text);

        foreach (var p in products)
        {
            searchTextParam.Value = SearchTextOf(p.Name, p.Sku, p.Barcode);
            idParam.Value = p.Id ?? string.Empty;
            nameParam.Value = p.Name ?? string.Empty;
            skuParam.Value = p.Sku ?? string.Empty;
            categoryParam.Value = p.Category ?? string.Empty;
            priceParam.Value = p.Price;
            origPriceParam.Value = p.OriginalPrice ?? (object)DBNull.Value;
            discountParam.Value = p.DiscountPercent ?? (object)DBNull.Value;
            imageParam.Value = p.ImagePath ?? string.Empty;
            barcodeParam.Value = p.Barcode ?? string.Empty;
            tagsParam.Value = JsonSerializer.Serialize(p.TagIds ?? new List<string>());
            unitIdParam.Value = p.UnitId ?? string.Empty;
            unitCodeParam.Value = p.UnitCode ?? string.Empty;
            unitShortNameParam.Value = p.UnitShortName ?? string.Empty;
            unitFactorParam.Value = p.UnitFactor;
            isDivisibleParam.Value = p.IsDivisible ? 1 : 0;
            sellInUnitParam.Value = p.SellInSecondaryUnit ? 1 : 0;

            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>The column list every product SELECT shares, in the order ReadProduct
    /// reads by ordinal. One constant because four copies of the same list is how the
    /// fifth one ends up different.</summary>
    private const string ProductColumns =
        "Id, Name, Sku, Category, Price, OriginalPrice, DiscountPercent, ImagePath, Barcode, Tags, "
        + "UnitId, UnitCode, UnitShortName, UnitFactor, IsDivisible, SellInSecondaryUnit, StockQuantity";

    private Product ReadProduct(SqliteDataReader reader)
    {
        return new Product
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Sku = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            Category = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            Price = reader.GetDecimal(4),
            OriginalPrice = reader.IsDBNull(5) ? null : reader.GetDecimal(5),
            DiscountPercent = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
            ImagePath = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            Barcode = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            TagIds = ReadTags(reader, 9),
            // Rows written before the unit migration have NULL here, so every
            // one of these falls back rather than throwing.
            UnitId = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            UnitCode = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            UnitShortName = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            UnitFactor = reader.IsDBNull(13) ? 0m : reader.GetDecimal(13),
            IsDivisible = !reader.IsDBNull(14) && reader.GetBoolean(14),
            SellInSecondaryUnit = !reader.IsDBNull(15) && reader.GetBoolean(15),
            // Ordinal 16, matching ProductColumns. NULL for a register that has never
            // completed a reconciliation walk.
            StockQuantity = reader.IsDBNull(16) ? null : reader.GetDecimal(16),
        };
    }

    /// <summary>Tags are a JSON array in one column. A row written before the Tags
    /// migration, or a malformed payload, reads as "no tags" rather than throwing —
    /// a broken tag list must not take the whole product catalog down.</summary>
    private static List<string> ReadTags(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return new List<string>();
        var raw = reader.GetString(ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    public async Task<IEnumerable<Product>> GetAllProductsAsync()
    {
        var products = new List<Product>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ProductColumns} FROM Products";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            products.Add(ReadProduct(reader));
        }

        return products;
    }

    public async Task<IEnumerable<Product>> GetProductsByCategoryAsync(string categoryId)
    {
        var products = new List<Product>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ProductColumns} FROM Products WHERE Category = $Category";
        command.Parameters.AddWithValue("$Category", categoryId);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            products.Add(ReadProduct(reader));
        }

        return products;
    }

    public async Task<IEnumerable<Product>> SearchProductsAsync(string query)
    {
        var products = new List<Product>();
        if (string.IsNullOrWhiteSpace(query)) return products;

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        // ESCAPE, because '%' and '_' are LIKE syntax and a cashier typing either means
        // the character — "50%" is a product name here, not "match everything".
        command.CommandText = $"SELECT {ProductColumns} FROM Products WHERE SearchText LIKE $Query ESCAPE '\\'";
        command.Parameters.AddWithValue("$Query", $"%{EscapeLike(query.Trim().ToLowerInvariant())}%");

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            products.Add(ReadProduct(reader));
        }

        return products;
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public async Task<Product?> GetProductByBarcodeAsync(string barcode)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ProductColumns} FROM Products WHERE Barcode = $Barcode LIMIT 1";
        command.Parameters.AddWithValue("$Barcode", barcode);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadProduct(reader);
        }

        return null;
    }

    private async Task SaveCategoriesInternalAsync(IEnumerable<Category> categories, int isQuickAccess)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // Note: we don't delete existing categories of this type, we just upsert.
        // If categories can be deleted on backend, a full sync would need a DELETE first.
        command.CommandText = @"
            INSERT INTO Categories (Id, Name, IsQuickAccess, ImageUrl, ParentId)
            VALUES ($Id, $Name, $IsQuickAccess, $ImageUrl, $ParentId)
            ON CONFLICT(Id) DO UPDATE SET
                Name=excluded.Name,
                IsQuickAccess=excluded.IsQuickAccess,
                ImageUrl=excluded.ImageUrl,
                ParentId=excluded.ParentId;
        ";

        var idParam = command.Parameters.Add("$Id", SqliteType.Text);
        var nameParam = command.Parameters.Add("$Name", SqliteType.Text);
        command.Parameters.AddWithValue("$IsQuickAccess", isQuickAccess);
        var imageUrlParam = command.Parameters.Add("$ImageUrl", SqliteType.Text);
        var parentIdParam = command.Parameters.Add("$ParentId", SqliteType.Text);

        foreach (var c in categories)
        {
            idParam.Value = c.Id ?? string.Empty;
            nameParam.Value = c.Name ?? string.Empty;
            imageUrlParam.Value = (object?)c.ImageUrl ?? DBNull.Value;
            parentIdParam.Value = (object?)c.Parent?.Id ?? DBNull.Value;
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public Task SaveCategoriesAsync(IEnumerable<Category> categories)
    {
        return SaveCategoriesInternalAsync(categories, 0);
    }

    public Task SaveQuickAccessCategoriesAsync(IEnumerable<Category> categories)
    {
        return SaveCategoriesInternalAsync(categories, 1);
    }

    private async Task<IEnumerable<Category>> GetCategoriesInternalAsync(int isQuickAccess)
    {
        var categories = new List<Category>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        if (isQuickAccess == 1)
        {
            command.CommandText = "SELECT Id, Name, ImageUrl, ParentId FROM Categories WHERE IsQuickAccess = 1";
        }
        else
        {
            // For all categories (isQuickAccess == 0), don't filter out the ones that happen to be quick access
            command.CommandText = "SELECT Id, Name, ImageUrl, ParentId FROM Categories";
        }

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            categories.Add(new Category
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                ImageUrl = reader.IsDBNull(2) ? null : reader.GetString(2),
                Parent = reader.IsDBNull(3) ? null : new CategoryRef { Id = reader.GetString(3) }
            });
        }

        return categories;
    }

    public Task<IEnumerable<Category>> GetCategoriesAsync()
    {
        return GetCategoriesInternalAsync(0);
    }

    public Task<IEnumerable<Category>> GetQuickAccessCategoriesAsync()
    {
        return GetCategoriesInternalAsync(1);
    }

    /// <summary>Stored as a Settings row rather than its own table: it is one
    /// value per register, and offline pricing must find it without a sync.</summary>
    public async Task SaveMoneyPolicyAsync(MoneyPolicy policy)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Settings (Key, Value) VALUES ('MoneyPolicy', $Value)
            ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;
        ";
        command.Parameters.AddWithValue("$Value", JsonSerializer.Serialize(policy));

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The cached policy, or the server's default when nothing was ever
    /// synced — same fallback the backend applies for an unconfigured store.</summary>
    public async Task<MoneyPolicy> GetMoneyPolicyAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = 'MoneyPolicy'";

        var result = await command.ExecuteScalarAsync();
        if (result is string raw && !string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                return JsonSerializer.Deserialize<MoneyPolicy>(raw) ?? MoneyPolicy.Default;
            }
            catch (JsonException)
            {
                return MoneyPolicy.Default;
            }
        }
        return MoneyPolicy.Default;
    }

    /// <summary>Stored as a Settings row rather than its own table, for the same
    /// reason as MoneyPolicy: it is one value per register, and the POS screen
    /// must know what to show before any network call completes.
    ///
    /// The dictionary is serialized on its own, not the CashFeatures wrapper, so
    /// the cached JSON is byte-for-byte what GET /cashes/features/ returns in its
    /// body — one shape to reason about instead of two.</summary>
    public async Task SaveCashFeaturesAsync(CashFeatures features)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Settings (Key, Value) VALUES ('CashFeatures', $Value)
            ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;
        ";
        command.Parameters.AddWithValue("$Value", JsonSerializer.Serialize(features.Flags));

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The cached flag map, or an empty one when nothing was ever synced
    /// — which CashFeatures reads as every function enabled. A damaged cache is
    /// treated the same way rather than thrown: a register must still open.</summary>
    public async Task<CashFeatures> GetCashFeaturesAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = 'CashFeatures'";

        var result = await command.ExecuteScalarAsync();
        if (result is string raw && !string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var flags = JsonSerializer.Deserialize<Dictionary<string, bool>>(raw);
                if (flags != null) return new CashFeatures { Flags = flags };
            }
            catch (JsonException)
            {
                // A corrupt cache must not stop the register from opening.
            }
        }

        return CashFeatures.Default;
    }

    public Task SaveReceiptTemplateAsync(string raw) => SaveSettingAsync("ReceiptTemplate", raw);

    public Task<string> GetReceiptTemplateAsync() => GetSettingAsync("ReceiptTemplate");

    /// <summary>A real ceiling with plenty of headroom: a legitimate 80mm-tape logo in
    /// base64 is tens of KB, so a couple of megabytes covers any real one while stopping
    /// a runaway value from growing offline_data.db forever — SQLite does not shrink the
    /// file back down on its own once a large row has been written and replaced.</summary>
    private const int MaxReceiptLogoBase64Length = 2 * 1024 * 1024;

    public Task SaveReceiptLogoAsync(string base64)
    {
        var value = base64 ?? string.Empty;
        if (value.Length > MaxReceiptLogoBase64Length)
        {
            throw new ArgumentException(
                $"Receipt logo is {value.Length} base64 characters, over the {MaxReceiptLogoBase64Length} limit.",
                nameof(base64));
        }

        return SaveSettingAsync("ReceiptLogo", value);
    }

    public Task<string> GetReceiptLogoAsync() => GetSettingAsync("ReceiptLogo");

    /// <summary>Shared by SaveReceiptTemplateAsync/SaveReceiptLogoAsync. This is now the
    /// fourth shape writing a Settings row (MoneyPolicy and CashFeatures above each have
    /// their own inline INSERT, and LastSyncVersion below has a fifth) — a candidate for
    /// one shared path across the class, not attempted here since that is a class-wide
    /// change and these helpers are private, so nothing outside this file depends on the
    /// duplication.
    ///
    /// Like every method in this class, this blocks the calling thread for the duration
    /// of the SQLite round trip rather than truly yielding — do not call it from a path
    /// (e.g. receipt printing) that needs the calling thread free while it awaits.
    ///
    /// value ?? string.Empty: raw arrives here straight from a JSON payload upstream
    /// (Task 10 onward), where a null property is legitimate. Without this, a null value
    /// fails inside SqliteParameter binding with "Value must be set" — a confusing error
    /// for what is really just an absent value that should be cached as empty.</summary>
    private async Task SaveSettingAsync(string key, string value)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Settings (Key, Value) VALUES ($Key, $Value)
            ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;
        ";
        command.Parameters.AddWithValue("$Key", key);
        command.Parameters.AddWithValue("$Value", value ?? string.Empty);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Fourth shape reading a Settings row, alongside MoneyPolicy, CashFeatures
    /// and LastSyncVersion above — same consolidation candidate as SaveSettingAsync,
    /// left alone for the same reason. Blocks the calling thread like every method in
    /// this class; see SaveSettingAsync.
    ///
    /// `as string ?? string.Empty`, not a null-forgiving cast: the interface promises
    /// Task&lt;string&gt; under nullable, and ExecuteScalarAsync returns null with no rows —
    /// the missing-key case every one of these settings has on a fresh database.</summary>
    private async Task<string> GetSettingAsync(string key)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = $Key";
        command.Parameters.AddWithValue("$Key", key);

        return await command.ExecuteScalarAsync() as string ?? string.Empty;
    }

    public async Task SetLastSyncVersionAsync(int version)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Settings (Key, Value) VALUES ('LastSyncVersion', $Version)
            ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;
        ";
        command.Parameters.AddWithValue("$Version", version.ToString());

        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> GetLastSyncVersionAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = 'LastSyncVersion'";

        var result = await command.ExecuteScalarAsync();
        if (result != null && int.TryParse(result.ToString(), out int version))
        {
            return version;
        }

        return 0;
    }


    public async Task SaveUnsyncedDocumentAsync(string hash, string payload)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO UnsyncedDocuments (Hash, Payload) VALUES ($Hash, $Payload)
            ON CONFLICT(Hash) DO UPDATE SET Payload=excluded.Payload;
        ";
        command.Parameters.AddWithValue("$Hash", hash);
        command.Parameters.AddWithValue("$Payload", payload);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<IEnumerable<KeyValuePair<string, string>>> GetUnsyncedDocumentsAsync()
    {
        var docs = new List<KeyValuePair<string, string>>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        // Rejected rows are deliberately excluded: they are what the register tried to
        // book, kept for the back office, not work still to be done. Counting them would
        // leave the unsynced badge permanently lit over a queue nothing can drain.
        command.CommandText = "SELECT Hash, Payload FROM UnsyncedDocuments WHERE RejectedAt IS NULL";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var hash = reader.GetString(0);
            var payload = reader.GetString(1);
            docs.Add(new KeyValuePair<string, string>(hash, payload));
        }

        return docs;
    }

    public async Task MarkDocumentRejectedAsync(string hash, string reason)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE UnsyncedDocuments
            SET RejectedAt = $RejectedAt, RejectedReason = $Reason
            WHERE Hash = $Hash;
        ";
        command.Parameters.AddWithValue("$Hash", hash);
        command.Parameters.AddWithValue("$RejectedAt", DateTime.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("$Reason", reason ?? string.Empty);

        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteUnsyncedDocumentAsync(string hash)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM UnsyncedDocuments WHERE Hash = $Hash";
        command.Parameters.AddWithValue("$Hash", hash);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Replaces the promotion cache wholesale. The endpoint returns the
    /// complete set, so an upsert would leave promotions that were disabled or
    /// deleted server-side still discounting carts on this register.</summary>
    public async Task SavePromotionsAsync(IEnumerable<Promotion> promotions)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = "DELETE FROM Promotions";
        await command.ExecuteNonQueryAsync();

        command.CommandText = "INSERT INTO Promotions (Id, Payload) VALUES ($Id, $Payload)";
        var idParam = command.Parameters.Add("$Id", SqliteType.Text);
        var payloadParam = command.Parameters.Add("$Payload", SqliteType.Text);

        foreach (var p in promotions)
        {
            if (string.IsNullOrWhiteSpace(p.Id)) continue;
            idParam.Value = p.Id;
            payloadParam.Value = JsonSerializer.Serialize(p);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task<IEnumerable<Promotion>> GetPromotionsAsync()
    {
        var promotions = new List<Promotion>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM Promotions";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            try
            {
                var p = JsonSerializer.Deserialize<Promotion>(reader.GetString(0));
                if (p != null) promotions.Add(p);
            }
            catch (JsonException)
            {
                // One unreadable row must not blank out every other promotion.
            }
        }
        return promotions;
    }

    public async Task ClearPromotionsAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Promotions";
        await command.ExecuteNonQueryAsync();
    }

    public async Task ClearCategoriesAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Categories";
        await command.ExecuteNonQueryAsync();
    }

    public async Task ClearProductsAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Products";
        await command.ExecuteNonQueryAsync();
    }

    public async Task ApplyRemainsAsync(IReadOnlyDictionary<string, decimal> remains)
    {
        // Empty map: refused. See the interface doc comment on ApplyRemainsAsync for why.
        if (remains.Count == 0)
            throw new ArgumentException("Refusing to apply an empty reconciliation result.", nameof(remains));

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();

        // A temp table rather than a giant IN (...) list: the catalogue runs to
        // thousands of rows and SQLite caps host parameters well below that.
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "CREATE TEMP TABLE IF NOT EXISTS RemainSeen (Id TEXT PRIMARY KEY NOT NULL, Qty TEXT NOT NULL);"
                            + "DELETE FROM RemainSeen;";
            await cmd.ExecuteNonQueryAsync();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "INSERT OR REPLACE INTO RemainSeen (Id, Qty) VALUES ($Id, $Qty);";
            var idParam = cmd.Parameters.Add("$Id", SqliteType.Text);
            var qtyParam = cmd.Parameters.Add("$Qty", SqliteType.Text);
            foreach (var (id, qty) in remains)
            {
                idParam.Value = id;
                qtyParam.Value = qty;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                DELETE FROM Products WHERE NOT EXISTS (SELECT 1 FROM RemainSeen WHERE RemainSeen.Id = Products.Id);
                UPDATE Products SET StockQuantity = (SELECT Qty FROM RemainSeen WHERE RemainSeen.Id = Products.Id);
                DROP TABLE IF EXISTS temp.RemainSeen;
            ";
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task SaveParkedSaleAsync(ParkedSale sale)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO ParkedSales (Id, Label, CustomerName, Total, ItemCount, CreatedAt, Payload)
            VALUES ($Id, $Label, $CustomerName, $Total, $ItemCount, $CreatedAt, $Payload)
            ON CONFLICT(Id) DO UPDATE SET
                Label=excluded.Label,
                CustomerName=excluded.CustomerName,
                Total=excluded.Total,
                ItemCount=excluded.ItemCount,
                CreatedAt=excluded.CreatedAt,
                Payload=excluded.Payload;
        ";
        command.Parameters.AddWithValue("$Id", sale.Id);
        command.Parameters.AddWithValue("$Label", (object?)sale.Label ?? DBNull.Value);
        command.Parameters.AddWithValue("$CustomerName", (object?)sale.CustomerName ?? DBNull.Value);
        command.Parameters.AddWithValue("$Total", sale.Total);
        command.Parameters.AddWithValue("$ItemCount", sale.ItemCount);
        command.Parameters.AddWithValue("$CreatedAt", sale.CreatedAt.ToString("o"));
        command.Parameters.AddWithValue("$Payload", sale.Payload);

        await command.ExecuteNonQueryAsync();
    }

    private ParkedSale ReadParkedSale(SqliteDataReader reader)
    {
        return new ParkedSale
        {
            Id = reader.GetString(0),
            Label = reader.IsDBNull(1) ? null : reader.GetString(1),
            CustomerName = reader.IsDBNull(2) ? null : reader.GetString(2),
            Total = reader.GetDecimal(3),
            ItemCount = reader.GetDecimal(4),
            CreatedAt = DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
            Payload = reader.GetString(6)
        };
    }

    public async Task<IEnumerable<ParkedSale>> GetParkedSalesAsync()
    {
        var sales = new List<ParkedSale>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Label, CustomerName, Total, ItemCount, CreatedAt, Payload FROM ParkedSales ORDER BY CreatedAt DESC";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sales.Add(ReadParkedSale(reader));
        }

        return sales;
    }

    public async Task<ParkedSale?> GetParkedSaleAsync(string id)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Label, CustomerName, Total, ItemCount, CreatedAt, Payload FROM ParkedSales WHERE Id = $Id LIMIT 1";
        command.Parameters.AddWithValue("$Id", id);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadParkedSale(reader);
        }

        return null;
    }

    public async Task DeleteParkedSaleAsync(string id)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ParkedSales WHERE Id = $Id";
        command.Parameters.AddWithValue("$Id", id);

        await command.ExecuteNonQueryAsync();
    }

    public async Task SaveSellersAsync(IEnumerable<SellerInfo> sellers)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();

        using var deleteCommand = connection.CreateCommand();
        deleteCommand.Transaction = transaction;
        deleteCommand.CommandText = "DELETE FROM Sellers";
        await deleteCommand.ExecuteNonQueryAsync();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO Sellers (Id, FirstName, LastName, PinHash, CanSell, CanRefund, CanCloseShift, MaxDiscount)
            VALUES ($Id, $FirstName, $LastName, $PinHash, $CanSell, $CanRefund, $CanCloseShift, $MaxDiscount);
        ";

        var idParam = command.Parameters.Add("$Id", SqliteType.Text);
        var firstNameParam = command.Parameters.Add("$FirstName", SqliteType.Text);
        var lastNameParam = command.Parameters.Add("$LastName", SqliteType.Text);
        var pinHashParam = command.Parameters.Add("$PinHash", SqliteType.Text);
        var canSellParam = command.Parameters.Add("$CanSell", SqliteType.Integer);
        var canRefundParam = command.Parameters.Add("$CanRefund", SqliteType.Integer);
        var canCloseShiftParam = command.Parameters.Add("$CanCloseShift", SqliteType.Integer);
        var maxDiscountParam = command.Parameters.Add("$MaxDiscount", SqliteType.Text);

        foreach (var s in sellers)
        {
            idParam.Value = s.Id ?? string.Empty;
            firstNameParam.Value = s.FirstName ?? string.Empty;
            lastNameParam.Value = s.LastName ?? string.Empty;
            pinHashParam.Value = s.PinHash ?? string.Empty;
            canSellParam.Value = s.CanSell ? 1 : 0;
            canRefundParam.Value = s.CanRefund ? 1 : 0;
            canCloseShiftParam.Value = s.CanCloseShift ? 1 : 0;
            maxDiscountParam.Value = s.MaxDiscount;

            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private SellerInfo ReadSeller(SqliteDataReader reader)
    {
        return new SellerInfo
        {
            Id = reader.GetString(0),
            FirstName = reader.GetString(1),
            LastName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            PinHash = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            CanSell = reader.GetInt32(4) != 0,
            CanRefund = reader.GetInt32(5) != 0,
            CanCloseShift = reader.GetInt32(6) != 0,
            MaxDiscount = reader.GetDecimal(7)
        };
    }

    public async Task<IEnumerable<SellerInfo>> GetSellersAsync()
    {
        var sellers = new List<SellerInfo>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, FirstName, LastName, PinHash, CanSell, CanRefund, CanCloseShift, MaxDiscount FROM Sellers";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sellers.Add(ReadSeller(reader));
        }

        return sellers;
    }
}
