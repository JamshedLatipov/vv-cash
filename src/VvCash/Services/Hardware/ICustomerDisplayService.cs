using System.Threading.Tasks;

namespace VvCash.Services.Hardware;

public interface ICustomerDisplayService
{
    Task ShowLineAsync(string line1, string line2);
    Task ShowItemAsync(string name, decimal price);
    Task ShowTotalAsync(decimal total);
    Task ClearAsync();
}
