using System;
using System.Threading.Tasks;

namespace VvCash.Services.Api;

public interface IShiftService
{
    Task<string?> OpenShiftAsync();
    Task<bool> CloseShiftAsync(string shiftId);
    Task<string?> GetShiftStateAsync();

    /// <summary>Raised when the server rejected the shift session (HTTP 401) while checking
    /// or opening a shift — never for a request that simply couldn't reach the server (that
    /// stays silent, same as before, so a register with no network keeps working offline).
    /// Mirrors <see cref="IExpenseDocumentService.SessionRevoked"/>, but PosViewModel reacts
    /// to this one differently: GetShiftStateAsync/OpenShiftAsync only ever run while nothing
    /// is mid-receipt (startup, or the shift modal blocking everything else), unlike a queued
    /// document that might be mid-sale, so there is nothing to protect by staying put — the
    /// register signs out and returns to login immediately instead of just raising a banner.</summary>
    event EventHandler? SessionRevoked;

    /// <summary>Raised when the server answered a shift operation with HTTP 403 — which is
    /// what this backend actually sends for a rejected session (middlewares/utils.go's
    /// redirectToAccessDenied), never 401, on any authenticated route.
    ///
    /// Deliberately separate from <see cref="SessionRevoked"/> rather than folded into it.
    /// Several backend paths produce a byte-identical 403 body — an expired JWT, an invalid
    /// Cash-Authorization token, a tenant-database pool failure, an inactive or deleted
    /// tenant, a missing is_seller row, a denied permission — and only the token ones mean
    /// the session is over. Treating 403 as a dead token would sign a cashier out over a
    /// database blip, or loop them through the login screen forever when the real fault is
    /// a misconfigured permission or a suspended tenant.
    /// PosViewModel therefore explains it inside the shift modal and leaves the decision to
    /// the cashier, instead of navigating away on its own.
    ///
    /// Never raised for a request that failed to reach the server — see
    /// <see cref="SessionRevoked"/>'s own remarks on why offline must stay silent.</summary>
    event EventHandler? AccessDenied;
}
