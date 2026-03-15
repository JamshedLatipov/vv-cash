using System.Threading.Tasks;

namespace VvCash.Services.Api;

public interface IShiftService
{
    Task<bool> OpenShiftAsync();
    Task<bool> CloseShiftAsync();
    Task<bool> GetShiftStateAsync();
}
