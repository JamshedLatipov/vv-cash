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
}
