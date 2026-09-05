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

    /// <summary>Books the sale into the offline queue and returns, without ever touching
    /// the network. What checkout uses.
    ///
    /// The two calls above put the round-trip on the interactive path: the receipt only
    /// printed once the server had answered, and on a shop connection that meant the
    /// cashier watching a dead button for up to the HttpClient timeout before the very
    /// same document was queued anyway. This does the queueing first and unconditionally,
    /// which is both instant and the behaviour the register already had to be correct
    /// under — a queued sale is replayed by <see cref="SyncOfflineDocumentsAsync"/>,
    /// exactly as it is for a genuine outage.
    ///
    /// The trade this makes is real and is why <see cref="DocumentRejected"/> exists: a
    /// server that refuses the document on its merits can no longer be answered to the
    /// cashier's face, only reported afterwards. Callers that must have the server's
    /// verdict before committing to anything — the exchange screen, which prints a
    /// document number — keep using
    /// <see cref="CreateExpenseDocumentDetailedAsync"/>.</summary>
    Task<ExpenseDocumentOutcome> QueueExpenseDocumentAsync(DocumentRequest request);

    Task SyncOfflineDocumentsAsync();
    Task<int> GetUnsyncedDocumentsCountAsync();
    event EventHandler<int>? UnsyncedDocumentsCountChanged;

    /// <summary>Raised when the server rejected the shift session (HTTP 401). The register
    /// keeps queueing receipts; only a banner is shown, never a forced logout mid-receipt.</summary>
    event EventHandler? SessionRevoked;

    /// <summary>Raised when replaying a queued sale meets a refusal on the merits — the
    /// one outcome no retry can fix. Carries the server's own reason so the till can say
    /// which sale and why; see <see cref="DocumentRejection"/> for why a silent database
    /// flag is not enough now that checkout does not wait for the server.
    ///
    /// Not raised for a retryable failure: a sale still queued is not news.</summary>
    event EventHandler<DocumentRejection>? DocumentRejected;
}
