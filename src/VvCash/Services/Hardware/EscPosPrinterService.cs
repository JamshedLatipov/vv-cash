using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Hardware;

public class EscPosPrinterService : IPrinterService
{
    private readonly PrinterConnectionType _connectionType;
    private readonly string _connectionString;
    private PrinterStatus _status = PrinterStatus.Ready;

    public PrinterStatus Status => _status;
    public event EventHandler<PrinterStatus>? StatusChanged;

    private static readonly byte[] CmdInit = { 0x1B, 0x40 };
    private static readonly byte[] CmdAlignLeft = { 0x1B, 0x61, 0x00 };
    private static readonly byte[] CmdAlignCenter = { 0x1B, 0x61, 0x01 };
    private static readonly byte[] CmdAlignRight = { 0x1B, 0x61, 0x02 };
    private static readonly byte[] CmdBoldOn = { 0x1B, 0x45, 0x01 };
    private static readonly byte[] CmdBoldOff = { 0x1B, 0x45, 0x00 };
    private static readonly byte[] CmdDoubleSizeOn = { 0x1B, 0x21, 0x30 };
    private static readonly byte[] CmdDoubleSizeOff = { 0x1B, 0x21, 0x00 };
    private static readonly byte[] CmdCut = { 0x1D, 0x56, 0x42, 0x00 };
    private static readonly byte[] CmdLineFeed = { 0x0A };
    public static readonly byte[] CmdDrawerKick = { 0x1B, 0x70, 0x00, 0x19, 0xFA };

    public EscPosPrinterService(PrinterConnectionType connectionType, string connectionString)
    {
        _connectionType = connectionType;
        _connectionString = connectionString;
    }

    /// <summary>Builds the sale receipt bytes. Static and separate from sending
    /// so the layout can be asserted on, exactly as BuildReturnReceipt is.</summary>
    public static byte[] BuildSaleReceipt(
        IEnumerable<CartItem> items, decimal subtotal, decimal discount, decimal total,
        string? discountName = null)
    {
        using var ms = new MemoryStream();
        Write(ms, CmdInit);
        Write(ms, CmdAlignCenter);
        Write(ms, CmdDoubleSizeOn);
        WriteLine(ms, "VV CASH POS");
        Write(ms, CmdDoubleSizeOff);
        WriteLine(ms, "----------------------------");
        Write(ms, CmdAlignLeft);
        foreach (var item in items)
        {
            var line = $"{item.Product.Name} x{item.QuantityDisplay}";
            var price = $"${item.LineTotal:F2}";
            WriteLine(ms, PadLine(line, price, 32));
        }
        WriteLine(ms, "----------------------------");
        WriteLine(ms, PadLine("Subtotal:", $"${subtotal:F2}", 32));
        if (discount > 0)
        {
            WriteLine(ms, PadLine("Discount:", $"-${discount:F2}", 32));
            if (!string.IsNullOrWhiteSpace(discountName))
                WriteLine(ms, Truncate(discountName!, 32));
        }

        Write(ms, CmdBoldOn);
        WriteLine(ms, PadLine("TOTAL:", $"${total:F2}", 32));
        Write(ms, CmdBoldOff);
        WriteLine(ms, "----------------------------");
        Write(ms, CmdAlignCenter);
        WriteLine(ms, "Thank you for shopping!");
        Write(ms, CmdLineFeed);
        Write(ms, CmdLineFeed);
        Write(ms, CmdCut);
        return ms.ToArray();
    }

    public async Task<bool> PrintReceiptAsync(IEnumerable<CartItem> items, decimal subtotal, decimal discount, decimal total, IEnumerable<Coupon> coupons, string? discountName = null)
    {
        try
        {
            await SendAsync(BuildSaleReceipt(items, subtotal, discount, total, discountName));
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Print error: {ex.Message}");
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }

    public async Task<bool> PrintPreReceiptAsync(IEnumerable<CartItem> items, decimal total)
    {
        try
        {
            using var ms = new MemoryStream();
            Write(ms, CmdInit);
            Write(ms, CmdAlignCenter);
            WriteLine(ms, "PRE-RECEIPT");
            WriteLine(ms, "----------------------------");
            Write(ms, CmdAlignLeft);
            foreach (var item in items)
                WriteLine(ms, $"  {item.Product.Name} x{item.QuantityDisplay}");
            WriteLine(ms, PadLine("TOTAL:", $"${total:F2}", 32));
            Write(ms, CmdLineFeed);
            Write(ms, CmdCut);
            await SendAsync(ms.ToArray());
            return true;
        }
        catch
        {
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }

    private static void Write(MemoryStream ms, byte[] data) => ms.Write(data, 0, data.Length);
    private static void WriteLine(MemoryStream ms, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        ms.Write(bytes, 0, bytes.Length);
    }
    private static string PadLine(string left, string right, int width)
    {
        var spaces = width - left.Length - right.Length;
        return left + new string(' ', Math.Max(1, spaces)) + right;
    }

    /// <summary>Clips a label to the paper width. A promotion name is free text and
    /// a long one would wrap into a ragged second line on a 32-column roll.</summary>
    private static string Truncate(string s, int width)
        => s.Length <= width ? s : s.Substring(0, width);

    private async Task SendAsync(byte[] data)
    {
        switch (_connectionType)
        {
            case PrinterConnectionType.COM:
                await SendViaCom(data);
                break;
            case PrinterConnectionType.LAN:
                await SendViaLan(data);
                break;
            case PrinterConnectionType.USB:
                await SendViaUsb(data);
                break;
            default:
                await Task.CompletedTask;
                break;
        }
    }

    private async Task SendViaCom(byte[] data)
    {
        using var port = new SerialPort(_connectionString, 9600);
        port.Open();
        await port.BaseStream.WriteAsync(data, 0, data.Length);
    }

    private async Task SendViaLan(byte[] data)
    {
        var parts = _connectionString.Split(':');
        var host = parts[0];
        var port = parts.Length > 1 ? int.Parse(parts[1]) : 9100;
        using var client = new TcpClient();
        await client.ConnectAsync(host, port);
        using var stream = client.GetStream();
        await stream.WriteAsync(data, 0, data.Length);
    }

    private Task SendViaUsb(byte[] data)
    {
        // For USB raw printing, integrating with Windows Spooler API via PInvoke is required.
        // As a fallback/stub for now, we just output to console to avoid crashing and to allow compilation.
        Console.WriteLine($"[USB Printer '{_connectionString}'] Outputting {data.Length} bytes.");
        return Task.CompletedTask;
    }

    private void SetStatus(PrinterStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    public static byte[] BuildReturnReceipt(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> lines,
        decimal totalRefund, string documentNumber)
    {
        using var ms = new MemoryStream();
        Write(ms, CmdInit);
        Write(ms, CmdAlignCenter);
        Write(ms, CmdDoubleSizeOn);
        WriteLine(ms, "RETURN / VOZVRAT");
        Write(ms, CmdDoubleSizeOff);
        WriteLine(ms, $"Doc #{documentNumber}");
        WriteLine(ms, "----------------------------");
        Write(ms, CmdAlignLeft);
        foreach (var l in lines)
            WriteLine(ms, PadLine($"{l.Name} x{l.Quantity}", $"{l.LineRefund:F2}", 32));
        WriteLine(ms, "----------------------------");
        Write(ms, CmdBoldOn);
        WriteLine(ms, PadLine("REFUND:", $"{totalRefund:F2}", 32));
        Write(ms, CmdBoldOff);
        Write(ms, CmdLineFeed);
        Write(ms, CmdLineFeed);
        Write(ms, CmdCut);
        return ms.ToArray();
    }

    public async Task<bool> PrintReturnReceiptAsync(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> lines,
        decimal totalRefund, string documentNumber)
    {
        try
        {
            await SendAsync(BuildReturnReceipt(lines, totalRefund, documentNumber));
            return true;
        }
        catch
        {
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }

    public async Task<bool> OpenCashDrawerAsync()
    {
        try
        {
            await SendAsync(CmdDrawerKick);
            return true;
        }
        catch
        {
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }

    /// <summary>Same ReturnReceiptLine shape as BuildReturnReceipt, split into a
    /// RETURNED and an ISSUED section, closed by a bold total line. The label
    /// carries the direction of <paramref name="difference"/> (customer owes vs.
    /// till refunds); only its absolute value is ever printed, so the cashier
    /// never has to read out a minus sign.</summary>
    public static byte[] BuildExchangeReceipt(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber)
    {
        using var ms = new MemoryStream();
        Write(ms, CmdInit);
        Write(ms, CmdAlignCenter);
        Write(ms, CmdDoubleSizeOn);
        WriteLine(ms, "EXCHANGE / OBMEN");
        Write(ms, CmdDoubleSizeOff);
        WriteLine(ms, $"Doc #{documentNumber}");
        WriteLine(ms, "----------------------------");
        Write(ms, CmdAlignLeft);

        WriteLine(ms, "RETURNED:");
        foreach (var l in returned)
            WriteLine(ms, PadLine($"{l.Name} x{l.Quantity}", $"{l.LineRefund:F2}", 32));

        WriteLine(ms, "ISSUED:");
        foreach (var l in issued)
            WriteLine(ms, PadLine($"{l.Name} x{l.Quantity}", $"{l.LineRefund:F2}", 32));

        WriteLine(ms, "----------------------------");
        Write(ms, CmdBoldOn);
        // An even swap owes nothing in either direction; without its own label it
        // printed "REFUND: 0.00" and invited the customer to ask for the money.
        var label = difference > 0 ? "AMOUNT DUE:" : difference < 0 ? "REFUND:" : "NO DIFFERENCE:";
        WriteLine(ms, PadLine(label, $"{Math.Abs(difference):F2}", 32));
        Write(ms, CmdBoldOff);
        Write(ms, CmdLineFeed);
        Write(ms, CmdLineFeed);
        Write(ms, CmdCut);
        return ms.ToArray();
    }

    public async Task<bool> PrintExchangeReceiptAsync(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber)
    {
        try
        {
            await SendAsync(BuildExchangeReceipt(returned, issued, difference, documentNumber));
            return true;
        }
        catch
        {
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }
}
