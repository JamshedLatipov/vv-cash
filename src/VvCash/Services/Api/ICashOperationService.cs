using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public interface ICashOperationService
{
    /// <summary>Hands money out of the till as an ordinary cash-expense document.
    /// Never throws: the caller runs this with a return already booked and needs a
    /// reason it can show, not an exception it has to guess at.</summary>
    Task<CashOpOutcome> CreateCashExpenseAsync(CashExpenseRequest request);
}
