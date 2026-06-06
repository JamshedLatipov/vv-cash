using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public interface IReturnService
{
    Task<ExpenseListResponse> GetSalesAsync(int page = 1);
    Task<ReturnDetailBody> GetReturnableLinesAsync(string expenseId);
    Task<bool> CreateReturnAsync(string expenseId, ReturnRequest request);
}
