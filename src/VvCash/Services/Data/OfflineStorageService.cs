using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Data;

public class OfflineStorageData
{
    public int LastSyncVersion { get; set; } = 0;
    public List<Product> Products { get; set; } = new();
    public List<Category> Categories { get; set; } = new();
    public List<Category> QuickAccessCategories { get; set; } = new();
}

public class OfflineStorageService : IOfflineStorageService
{
    private readonly string _storagePath;
    private OfflineStorageData _data = new();
    private readonly object _lock = new();
    private bool _isInitialized = false;
    private Dictionary<string, Product> _productDictionary = new();

    public OfflineStorageService()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDir = Path.Combine(appDataPath, "VvCash");
        Directory.CreateDirectory(appDir);
        _storagePath = Path.Combine(appDir, "offline_data.json");
    }

    public Task InitializeAsync()
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_isInitialized) return;

                if (File.Exists(_storagePath))
                {
                    try
                    {
                        var json = File.ReadAllText(_storagePath);
                        _data = JsonSerializer.Deserialize<OfflineStorageData>(json) ?? new OfflineStorageData();
                    }
                    catch
                    {
                        _data = new OfflineStorageData();
                    }
                }
                else
                {
                    _data = new OfflineStorageData();
                }

                // Rebuild internal dictionary for fast lookups
                _productDictionary.Clear();
                foreach (var p in _data.Products)
                {
                    _productDictionary[p.Id] = p;
                }

                _isInitialized = true;
            }
        });
    }

    private Task SaveChangesAsync()
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                try
                {
                    var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_storagePath, json);
                }
                catch
                {
                    // Ignore save errors for now
                }
            }
        });
    }

    public async Task SaveProductsAsync(IEnumerable<Product> products)
    {
        lock (_lock)
        {
            foreach (var p in products)
            {
                if (_productDictionary.ContainsKey(p.Id))
                {
                    var existingIndex = _data.Products.FindIndex(x => x.Id == p.Id);
                    if (existingIndex >= 0)
                    {
                        _data.Products[existingIndex] = p;
                    }
                }
                else
                {
                    _data.Products.Add(p);
                }

                _productDictionary[p.Id] = p;
            }
        }
        await SaveChangesAsync();
    }

    public Task<IEnumerable<Product>> GetAllProductsAsync()
    {
        lock (_lock)
        {
            return Task.FromResult<IEnumerable<Product>>(_data.Products.ToList());
        }
    }

    public Task<IEnumerable<Product>> GetProductsByCategoryAsync(string categoryId)
    {
        lock (_lock)
        {
            return Task.FromResult<IEnumerable<Product>>(_data.Products.Where(p => p.Category == categoryId).ToList());
        }
    }

    public Task<Product?> GetProductByBarcodeAsync(string barcode)
    {
        lock (_lock)
        {
            return Task.FromResult(_data.Products.FirstOrDefault(p => p.Barcode == barcode));
        }
    }

    public async Task SaveCategoriesAsync(IEnumerable<Category> categories)
    {
        lock (_lock)
        {
            _data.Categories = categories.ToList();
        }
        await SaveChangesAsync();
    }

    public Task<IEnumerable<Category>> GetCategoriesAsync()
    {
        lock (_lock)
        {
            return Task.FromResult<IEnumerable<Category>>(_data.Categories.ToList());
        }
    }

    public async Task SaveQuickAccessCategoriesAsync(IEnumerable<Category> categories)
    {
        lock (_lock)
        {
            _data.QuickAccessCategories = categories.ToList();
        }
        await SaveChangesAsync();
    }

    public Task<IEnumerable<Category>> GetQuickAccessCategoriesAsync()
    {
        lock (_lock)
        {
            return Task.FromResult<IEnumerable<Category>>(_data.QuickAccessCategories.ToList());
        }
    }

    public async Task SetLastSyncVersionAsync(int version)
    {
        lock (_lock)
        {
            _data.LastSyncVersion = version;
        }
        await SaveChangesAsync();
    }

    public Task<int> GetLastSyncVersionAsync()
    {
        lock (_lock)
        {
            return Task.FromResult(_data.LastSyncVersion);
        }
    }
}
