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
    /// <param name="warehouseName">The warehouse/store the original sale was rung
    /// up from. Null or empty prints no such line.</param>
    /// <param name="sellerName">Who made the original sale. Null or empty prints
    /// no such line.</param>
    /// <param name="saleDate">Already formatted for display — this layer prints it
    /// verbatim rather than parsing it itself. Null or empty prints no such line.</param>
    System.Threading.Tasks.Task<bool> PrintReturnReceiptAsync(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> lines,
        decimal totalRefund, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null);

    /// <param name="difference">Positive: the customer owes the difference.
    /// Negative: the till refunds it. Only its absolute value is printed — the
    /// label carries the sign.</param>
    /// <param name="warehouseName">The warehouse/store the original sale was rung
    /// up from. Null or empty prints no such line.</param>
    /// <param name="sellerName">Who made the original sale. Null or empty prints
    /// no such line.</param>
    /// <param name="saleDate">Already formatted for display. Null or empty prints
    /// no such line.</param>
    System.Threading.Tasks.Task<bool> PrintExchangeReceiptAsync(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null);
}
