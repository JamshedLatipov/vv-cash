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

    /// <summary>Sets a PIN for a seller who has never had one (<see cref="SellerInfo.HasPin"/>
    /// == false) — first-time PIN setup for a new hire, done by whoever is holding the
    /// shift (their token, not the target seller's). POSTs to the admin PIN-reset
    /// endpoint rather than a self-service one, since this is one seller setting a PIN
    /// for another. On success, refreshes the roster so the newly cached hash is
    /// available immediately. Never throws — same discipline as <see cref="RefreshAsync"/> —
    /// any failure (network, non-2xx, malformed envelope) is reported as false.</summary>
    Task<bool> SetPinAsync(string sellerId, string pin);
}
