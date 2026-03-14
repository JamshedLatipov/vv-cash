using System;
using System.Threading.Tasks;

namespace VvCash.Services.Hardware;

public class MockCustomerDisplayService : ICustomerDisplayService
{
    public Task ShowLineAsync(string line1, string line2) { Console.WriteLine($"[Display] {line1} | {line2}"); return Task.CompletedTask; }
    public Task ShowItemAsync(string name, decimal price) { Console.WriteLine($"[Display] {name} ${price:F2}"); return Task.CompletedTask; }
    public Task ShowTotalAsync(decimal total) { Console.WriteLine($"[Display] TOTAL: ${total:F2}"); return Task.CompletedTask; }
    public Task ClearAsync() { Console.WriteLine("[Display] Clear"); return Task.CompletedTask; }
}
