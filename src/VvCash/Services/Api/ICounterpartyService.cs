using System.Threading.Tasks;
using System.Collections.Generic;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public interface ICounterpartyService
{
    Task<CounterpartyResponse?> CreateCounterpartyAsync(CounterpartyCreateRequest request);
    Task<List<CounterpartyResponse>?> SearchCounterpartiesAsync(string query);
}
