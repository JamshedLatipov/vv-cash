using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public interface ICounterpartyService
{
    Task<CounterpartyResponse?> CreateCounterpartyAsync(CounterpartyCreateRequest request);
}
