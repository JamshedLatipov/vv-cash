using System.Threading.Tasks;
using System.Collections.Generic;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public interface ICounterpartyService
{
    Task<CounterpartyResponse?> CreateCounterpartyAsync(CounterpartyCreateRequest request);
    Task<List<CounterpartyResponse>?> SearchCounterpartiesAsync(string query);

    /// <summary>Id of the store's system counterparty — the party every register sale
    /// is booked against, because the register never sends one and the server defaults
    /// an expense with no counterparty to it (DocumentExpenseSerializer.Validate). A
    /// return inherits its parent sale's counterparty, so naming the same one on the
    /// exchange's till payout is what keeps all three legs on one ledger.
    ///
    /// Null when the server cannot be reached or answers with no system counterparty.</summary>
    Task<string?> GetSystemCounterpartyIdAsync();
}
