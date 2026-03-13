using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Hardware;

public interface IPrinterService
{
    PrinterStatus Status { get; }
    event EventHandler<PrinterStatus>? StatusChanged;
    Task<bool> PrintReceiptAsync(IEnumerable<CartItem> items, decimal subtotal, decimal tax, decimal discount, decimal total, IEnumerable<Coupon> coupons);
    Task<bool> PrintPreReceiptAsync(IEnumerable<CartItem> items, decimal total);
}
