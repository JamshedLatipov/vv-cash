using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Api;

public interface ISellerRosterService
{
    /// <summary>Fetches the roster from the server and caches it. On any network or
    /// parse failure returns the cached roster instead, so the register keeps working.</summary>
    Task<IEnumerable<SellerInfo>> RefreshAsync();

    /// <summary>Returns the cached roster without touching the network.</summary>
    Task<IEnumerable<SellerInfo>> GetCachedAsync();
}
