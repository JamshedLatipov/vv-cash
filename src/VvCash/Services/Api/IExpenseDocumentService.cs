using System;
using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public interface IExpenseDocumentService
{
    Task<bool> CreateExpenseDocumentAsync(DocumentRequest request);
    Task SyncOfflineDocumentsAsync();
    Task<int> GetUnsyncedDocumentsCountAsync();
    event EventHandler<int>? UnsyncedDocumentsCountChanged;
}
