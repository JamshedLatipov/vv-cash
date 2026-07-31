using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public interface IReturnService
{
    /// <param name="documentNumber">Exact receipt number, or null/blank to browse. The
    /// backend drops its default today-only date range when this is given, so a slip from
    /// any earlier day is still findable — see ExpenseFilterBuilder.ApplyFilter. The
    /// result stays scoped to this register's own cash either way.</param>
    Task<ExpenseListResponse> GetSalesAsync(int page = 1, string? documentNumber = null);
    Task<ReturnDetailBody> GetReturnableLinesAsync(string expenseId);
    Task<bool> CreateReturnAsync(string expenseId, ReturnRequest request);
}
