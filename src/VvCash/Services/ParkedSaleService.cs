using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Data;

namespace VvCash.Services;

public class ParkedSaleService : IParkedSaleService
{
    private readonly IOfflineStorageService _storage;

    public event EventHandler<int>? CountChanged;

    public ParkedSaleService(IOfflineStorageService storage)
    {
        _storage = storage;
    }

    public async Task<ParkedSale> ParkAsync(ParkedSaleSnapshot snapshot, decimal total)
    {
        var sale = new ParkedSale
        {
            Id = Guid.NewGuid().ToString(),
            Label = string.IsNullOrWhiteSpace(snapshot.Label) ? null : snapshot.Label.Trim(),
            CustomerName = snapshot.Customer?.FullName,
            Total = total,
            ItemCount = snapshot.Items.Sum(i => i.Quantity),
            CreatedAt = DateTime.Now,
            Payload = JsonSerializer.Serialize(snapshot)
        };

        await _storage.SaveParkedSaleAsync(sale);
        await RaiseCountChangedAsync();
        return sale;
    }

    public async Task<IReadOnlyList<ParkedSale>> GetAllAsync()
    {
        var sales = await _storage.GetParkedSalesAsync();
        return sales.ToList();
    }

    public async Task<ParkedSaleSnapshot?> ResumeAsync(string id)
    {
        var sale = await _storage.GetParkedSaleAsync(id);
        if (sale == null) return null;

        ParkedSaleSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<ParkedSaleSnapshot>(sale.Payload);
        }
        catch (JsonException)
        {
            // Битый payload — удаляем, чтобы не блокировать список.
            await _storage.DeleteParkedSaleAsync(id);
            await RaiseCountChangedAsync();
            return null;
        }

        await _storage.DeleteParkedSaleAsync(id);
        await RaiseCountChangedAsync();
        return snapshot;
    }

    public async Task DeleteAsync(string id)
    {
        await _storage.DeleteParkedSaleAsync(id);
        await RaiseCountChangedAsync();
    }

    public async Task<int> GetCountAsync()
    {
        var sales = await _storage.GetParkedSalesAsync();
        return sales.Count();
    }

    private async Task RaiseCountChangedAsync()
    {
        var count = await GetCountAsync();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => CountChanged?.Invoke(this, count));
    }
}
