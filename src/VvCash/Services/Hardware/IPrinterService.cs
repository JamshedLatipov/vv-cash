using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Hardware;

public interface IPrinterService
{
    PrinterStatus Status { get; }
    event EventHandler<PrinterStatus>? StatusChanged;
    /// <param name="discountName">Label of the discount source (promotion name,
    /// promo code, card) printed under the discount line so the customer can see
    /// why the total dropped. Null or empty prints no such line.</param>
    Task<bool> PrintReceiptAsync(IEnumerable<CartItem> items, decimal subtotal, decimal discount, decimal total, IEnumerable<Coupon> coupons, string? discountName = null);
    Task<bool> PrintPreReceiptAsync(IEnumerable<CartItem> items, decimal total);
    System.Threading.Tasks.Task<bool> OpenCashDrawerAsync();
    System.Threading.Tasks.Task<bool> PrintReturnReceiptAsync(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> lines,
        decimal totalRefund, string documentNumber);
}
