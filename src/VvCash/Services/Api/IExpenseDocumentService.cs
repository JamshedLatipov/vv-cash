using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public interface IExpenseDocumentService
{
    Task<bool> CreateExpenseDocumentAsync(DocumentRequest request);
}
