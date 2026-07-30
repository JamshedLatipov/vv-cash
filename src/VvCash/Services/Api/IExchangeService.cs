using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public interface IExchangeService
{
    /// <summary>Posts an exchange. <see cref="ExchangeOutcome.Body"/> is non-null
    /// only when the server booked it — callers must not print a receipt or clear
    /// the screen otherwise — and the refusal's own status and reason come back
    /// alongside it, so the cashier can be told why instead of just "failed".</summary>
    Task<ExchangeOutcome> CreateExchangeAsync(string expenseDocumentId, ExchangeRequest request);
}
