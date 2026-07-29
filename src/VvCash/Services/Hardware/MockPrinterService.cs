using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Hardware;

public class MockPrinterService : IPrinterService
{
    public PrinterStatus Status => PrinterStatus.Ready;
    public event EventHandler<PrinterStatus>? StatusChanged;

    public Task<bool> PrintReceiptAsync(IEnumerable<CartItem> items, decimal subtotal, decimal discount, decimal total, IEnumerable<Coupon> coupons, string? discountName = null)
    {
        Console.WriteLine("=== RECEIPT ===");
        foreach (var item in items)
            Console.WriteLine($"  {item.Product.Name} x{item.QuantityDisplay}  ${item.LineTotal:F2}");
        Console.WriteLine($"Subtotal: ${subtotal:F2}");
        Console.WriteLine($"Discount: -${discount:F2}");
        if (!string.IsNullOrWhiteSpace(discountName))
            Console.WriteLine($"  ({discountName})");

        Console.WriteLine($"TOTAL: ${total:F2}");
        Console.WriteLine("===============");
        return Task.FromResult(true);
    }

    public Task<bool> PrintPreReceiptAsync(IEnumerable<CartItem> items, decimal total)
    {
        Console.WriteLine("=== PRE-RECEIPT ===");
        foreach (var item in items)
            Console.WriteLine($"  {item.Product.Name} x{item.QuantityDisplay}");
        Console.WriteLine($"TOTAL: ${total:F2}");
        return Task.FromResult(true);
    }

    public Task<bool> OpenCashDrawerAsync()
    {
        Console.WriteLine("[MockPrinter] Cash drawer kick");
        return Task.FromResult(true);
    }

    public Task<bool> PrintReturnReceiptAsync(
        IEnumerable<VvCash.Models.ReturnReceiptLine> lines, decimal totalRefund, string documentNumber)
    {
        Console.WriteLine($"=== RETURN #{documentNumber} ===");
        foreach (var l in lines)
            Console.WriteLine($"  {l.Name} x{l.Quantity}  {l.LineRefund:F2}");
        Console.WriteLine($"REFUND: {totalRefund:F2}");
        Console.WriteLine("===============");
        return Task.FromResult(true);
    }

    public Task<bool> PrintExchangeReceiptAsync(
        IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber)
    {
        Console.WriteLine($"=== EXCHANGE #{documentNumber} ===");
        Console.WriteLine("RETURNED:");
        foreach (var l in returned)
            Console.WriteLine($"  {l.Name} x{l.Quantity}  {l.LineRefund:F2}");
        Console.WriteLine("ISSUED:");
        foreach (var l in issued)
            Console.WriteLine($"  {l.Name} x{l.Quantity}  {l.LineRefund:F2}");
        Console.WriteLine(difference > 0 ? $"AMOUNT DUE: {difference:F2}" : $"REFUND: {-difference:F2}");
        Console.WriteLine("===============");
        return Task.FromResult(true);
    }
}
