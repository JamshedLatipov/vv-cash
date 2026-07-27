using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Data;

namespace VvCash.Services.Discounts;

/// <summary>In-memory view of everything offline pricing needs: the cached
/// promotion set and the store's money rounding. Cart pricing runs on every
/// keystroke-level change, so it cannot await SQLite each time; this holds the
/// snapshot and is refreshed after each sync.</summary>
public interface IPromotionProvider
{
    IReadOnlyList<Promotion> Promotions { get; }

    /// <summary>Store money rounding. Falls back to the server's default until
    /// the first sync brings the real one.</summary>
    MoneyPolicy MoneyPolicy { get; }

    Task RefreshAsync();
}

public sealed class PromotionProvider : IPromotionProvider
{
    private readonly IOfflineStorageService _storage;
    private IReadOnlyList<Promotion> _promotions = Array.Empty<Promotion>();
    private MoneyPolicy _moneyPolicy = MoneyPolicy.Default;

    public PromotionProvider(IOfflineStorageService storage) => _storage = storage;

    public IReadOnlyList<Promotion> Promotions => _promotions;
    public MoneyPolicy MoneyPolicy => _moneyPolicy;

    public async Task RefreshAsync()
    {
        try
        {
            _promotions = (await _storage.GetPromotionsAsync()).ToList();
            _moneyPolicy = await _storage.GetMoneyPolicyAsync();
        }
        catch (Exception ex)
        {
            // Keep the previous snapshot: pricing with a stale promotion set beats
            // silently dropping every promotion because one read failed.
            Debug.WriteLine($"[PromotionProvider] Refresh failed: {ex.Message}");
        }
    }
}
