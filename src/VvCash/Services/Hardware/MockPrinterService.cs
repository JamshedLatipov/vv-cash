using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Hardware;

public class MockPrinterService : IPrinterService
{
    public PrinterStatus Status => PrinterStatus.Ready;
    public event EventHandler<PrinterStatus>? StatusChanged;

    public Task<bool> PrintReceiptAsync(IEnumerable<CartItem> items, decimal subtotal, decimal tax, decimal discount, decimal total, IEnumerable<Coupon> coupons)
    {
        Console.WriteLine("=== RECEIPT ===");
        foreach (var item in items)
            Console.WriteLine($"  {item.Product.Name} x{item.Quantity}  ${item.LineTotal:F2}");
        Console.WriteLine($"Subtotal: ${subtotal:F2}");
        Console.WriteLine($"Discount: -${discount:F2}");
        Console.WriteLine($"Tax: ${tax:F2}");
        Console.WriteLine($"TOTAL: ${total:F2}");
        Console.WriteLine("===============");
        return Task.FromResult(true);
    }

    public Task<bool> PrintPreReceiptAsync(IEnumerable<CartItem> items, decimal total)
    {
        Console.WriteLine("=== PRE-RECEIPT ===");
        foreach (var item in items)
            Console.WriteLine($"  {item.Product.Name} x{item.Quantity}");
        Console.WriteLine($"TOTAL: ${total:F2}");
        return Task.FromResult(true);
    }
}
