using System.Threading.Tasks;

namespace VvCash.Services.Api;

public interface IAuthService
{
    Task<bool> LoginAsync(string email, string password, bool rememberMe);

    /// <summary>Wipes the stored auth token and its expiry, then persists the change.
    /// AuthToken/AuthTokenExpiresAt are written only here and by <see cref="LoginAsync"/> —
    /// the token's real lifetime is the shift, not a fixed window (see LoginAsync's own
    /// remarks), so this is what a successful shift close calls to end that session.</summary>
    void ClearSession();
}
