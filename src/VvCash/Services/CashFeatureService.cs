using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Data;

namespace VvCash.Services;

public class CashFeatureService : ICashFeatureService
{
    private readonly IOfflineStorageService _storage;

    public CashFeatureService(IOfflineStorageService storage) => _storage = storage;

    /// <summary>Starts as the all-enabled default rather than null: a register
    /// mid-startup must render a working screen, not throw.</summary>
    public CashFeatures Current { get; private set; } = CashFeatures.Default;

    public bool HasLoaded { get; private set; }

    public async Task RefreshAsync()
    {
        Current = await _storage.GetCashFeaturesAsync();
        HasLoaded = true;
    }
}
