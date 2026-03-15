using System.Threading.Tasks;

namespace VvCash.Services.Api;

public interface IShiftService
{
    Task<string?> OpenShiftAsync();
    Task<bool> CloseShiftAsync(string shiftId);
    Task<string?> GetShiftStateAsync();
}
