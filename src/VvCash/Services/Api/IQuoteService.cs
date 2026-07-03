using System.Threading;
using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public interface IQuoteService
{
    Task<QuoteResult?> QuoteAsync(QuoteRequest request, CancellationToken ct);
}
