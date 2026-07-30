using System;
using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public interface IExpenseDocumentService
{
    Task<bool> CreateExpenseDocumentAsync(DocumentRequest request);

    /// <summary>The same call, but saying whether the sale actually reached the server
    /// or went into the offline queue, and carrying the server's document number when
    /// it did. <see cref="CreateExpenseDocumentAsync"/> collapses both into true, which
    /// is right for the checkout path (the cashier may carry on either way) and wrong
    /// for the exchange screen, which has to tell the cashier which of the two it
    /// was.</summary>
    Task<ExpenseDocumentOutcome> CreateExpenseDocumentDetailedAsync(DocumentRequest request);
    Task SyncOfflineDocumentsAsync();
    Task<int> GetUnsyncedDocumentsCountAsync();
    event EventHandler<int>? UnsyncedDocumentsCountChanged;

    /// <summary>Raised when the server rejected the shift session (HTTP 401). The register
    /// keeps queueing receipts; only a banner is shown, never a forced logout mid-receipt.</summary>
    event EventHandler? SessionRevoked;
}
