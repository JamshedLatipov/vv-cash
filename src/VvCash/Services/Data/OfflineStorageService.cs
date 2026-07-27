using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Data.Sqlite;
using VvCash.Models;

namespace VvCash.Services.Data;

public class OfflineStorageService : IOfflineStorageService
{
    private readonly string _connectionString;
    private bool _isInitialized = false;

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

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();

        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT
            );

            CREATE TABLE IF NOT EXISTS UnsyncedDocuments (
                Hash TEXT PRIMARY KEY,
                Payload TEXT
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
                Price REAL NOT NULL,
                OriginalPrice REAL,
                DiscountPercent REAL,
                ImagePath TEXT,
                Barcode TEXT
            );

            CREATE TABLE IF NOT EXISTS ParkedSales (
                Id TEXT PRIMARY KEY,
                Label TEXT,
                CustomerName TEXT,
                Total REAL NOT NULL,
                ItemCount INTEGER NOT NULL,
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
                MaxDiscount REAL NOT NULL DEFAULT 0
            );

            -- Create indices for performance
            CREATE INDEX IF NOT EXISTS IDX_Products_Category ON Products(Category);
            CREATE INDEX IF NOT EXISTS IDX_Products_Barcode ON Products(Barcode);
        ";

        await command.ExecuteNonQueryAsync();

        // Ensure LastSyncVersion setting exists
        command.CommandText = "INSERT OR IGNORE INTO Settings (Key, Value) VALUES ('LastSyncVersion', '0');";
        await command.ExecuteNonQueryAsync();

        // Migration: add ImageUrl to Categories if upgrading from older DB
        try
        {
            command.CommandText = "ALTER TABLE Categories ADD COLUMN ImageUrl TEXT;";
            await command.ExecuteNonQueryAsync();
        }
        catch { /* column already exists */ }

        // Migration: add ParentId to Categories if upgrading from older DB
        try
        {
            command.CommandText = "ALTER TABLE Categories ADD COLUMN ParentId TEXT;";
            await command.ExecuteNonQueryAsync();
        }
        catch { /* column already exists */ }

        _isInitialized = true;
    }

    public async Task SaveProductsAsync(IEnumerable<Product> products)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = @"
            INSERT INTO Products (Id, Name, Sku, Category, Price, OriginalPrice, DiscountPercent, ImagePath, Barcode)
            VALUES ($Id, $Name, $Sku, $Category, $Price, $OriginalPrice, $DiscountPercent, $ImagePath, $Barcode)
            ON CONFLICT(Id) DO UPDATE SET
                Name=excluded.Name,
                Sku=excluded.Sku,
                Category=excluded.Category,
                Price=excluded.Price,
                OriginalPrice=excluded.OriginalPrice,
                DiscountPercent=excluded.DiscountPercent,
                ImagePath=excluded.ImagePath,
                Barcode=excluded.Barcode;
        ";

        var idParam = command.Parameters.Add("$Id", SqliteType.Text);
        var nameParam = command.Parameters.Add("$Name", SqliteType.Text);
        var skuParam = command.Parameters.Add("$Sku", SqliteType.Text);
        var categoryParam = command.Parameters.Add("$Category", SqliteType.Text);
        var priceParam = command.Parameters.Add("$Price", SqliteType.Real);
        var origPriceParam = command.Parameters.Add("$OriginalPrice", SqliteType.Real);
        var discountParam = command.Parameters.Add("$DiscountPercent", SqliteType.Real);
        var imageParam = command.Parameters.Add("$ImagePath", SqliteType.Text);
        var barcodeParam = command.Parameters.Add("$Barcode", SqliteType.Text);

        foreach (var p in products)
        {
            idParam.Value = p.Id ?? string.Empty;
            nameParam.Value = p.Name ?? string.Empty;
            skuParam.Value = p.Sku ?? string.Empty;
            categoryParam.Value = p.Category ?? string.Empty;
            priceParam.Value = p.Price;
            origPriceParam.Value = p.OriginalPrice ?? (object)DBNull.Value;
            discountParam.Value = p.DiscountPercent ?? (object)DBNull.Value;
            imageParam.Value = p.ImagePath ?? string.Empty;
            barcodeParam.Value = p.Barcode ?? string.Empty;

            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

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
            Barcode = reader.IsDBNull(8) ? string.Empty : reader.GetString(8)
        };
    }

    public async Task<IEnumerable<Product>> GetAllProductsAsync()
    {
        var products = new List<Product>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Sku, Category, Price, OriginalPrice, DiscountPercent, ImagePath, Barcode FROM Products";

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
        command.CommandText = "SELECT Id, Name, Sku, Category, Price, OriginalPrice, DiscountPercent, ImagePath, Barcode FROM Products WHERE Category = $Category";
        command.Parameters.AddWithValue("$Category", categoryId);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            products.Add(ReadProduct(reader));
        }

        return products;
    }

    public async Task<Product?> GetProductByBarcodeAsync(string barcode)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Sku, Category, Price, OriginalPrice, DiscountPercent, ImagePath, Barcode FROM Products WHERE Barcode = $Barcode LIMIT 1";
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
        command.CommandText = "SELECT Hash, Payload FROM UnsyncedDocuments";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var hash = reader.GetString(0);
            var payload = reader.GetString(1);
            docs.Add(new KeyValuePair<string, string>(hash, payload));
        }

        return docs;
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

    public async Task ClearUnsyncedDocumentsAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM UnsyncedDocuments";
        await command.ExecuteNonQueryAsync();
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
            ItemCount = reader.GetInt32(4),
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
        var maxDiscountParam = command.Parameters.Add("$MaxDiscount", SqliteType.Real);

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
