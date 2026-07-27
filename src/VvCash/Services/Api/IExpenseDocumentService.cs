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

    /// <summary>Raised when the server rejected the shift session (HTTP 401). The register
    /// keeps queueing receipts; only a banner is shown, never a forced logout mid-receipt.</summary>
    event EventHandler? SessionRevoked;
}
