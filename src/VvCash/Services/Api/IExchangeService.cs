using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public interface IExchangeService
{
    /// <summary>Posts an exchange. Returns null when the server refused it —
    /// callers must not print a receipt or clear the screen in that case.</summary>
    Task<ExchangeResponseBody?> CreateExchangeAsync(string expenseDocumentId, ExchangeRequest request);
}
