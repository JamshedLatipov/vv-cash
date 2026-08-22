using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Hardware;

public class EscPosPrinterService : IPrinterService
{
    private readonly PrinterConnectionType _connectionType;
    private readonly string _connectionString;
    private readonly EscPosCodePage _codePage;
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

    public EscPosPrinterService(PrinterConnectionType connectionType, string connectionString,
        EscPosCodePage codePage)
    {
        _connectionType = connectionType;
        _connectionString = connectionString;
        _codePage = codePage;
    }

    /// <summary>Builds the sale receipt bytes. Static and separate from sending
    /// so the layout can be asserted on, exactly as BuildReturnReceipt is.</summary>
    public static byte[] BuildSaleReceipt(
        EscPosCodePage codePage,
        IEnumerable<CartItem> items, decimal subtotal, decimal discount, decimal total,
        string? discountName = null,
        string? documentNumber = null, string? warehouseName = null,
        string? sellerName = null, string? saleDate = null)
    {
        using var ms = new MemoryStream();
        WriteInit(ms, codePage);
        Write(ms, CmdAlignCenter);
        Write(ms, CmdDoubleSizeOn);
        WriteLine(ms, "VV CASH POS", codePage);
        Write(ms, CmdDoubleSizeOff);
        // The same four facts the return and exchange receipts carry, and for the same
        // reason: without them a sale receipt brought back to the till cannot be matched
        // to its document. Each is omitted when absent rather than printed empty — an
        // offline sale has no document number yet, and a register with seller switching
        // off has no seller to name.
        if (!string.IsNullOrWhiteSpace(documentNumber)) WriteLine(ms, $"Doc #{documentNumber}", codePage);
        if (!string.IsNullOrWhiteSpace(saleDate)) WriteLine(ms, saleDate!, codePage);
        if (!string.IsNullOrWhiteSpace(warehouseName)) WriteLine(ms, $"Whse: {warehouseName}", codePage);
        if (!string.IsNullOrWhiteSpace(sellerName)) WriteLine(ms, $"Seller: {sellerName}", codePage);
        WriteLine(ms, "----------------------------", codePage);
        Write(ms, CmdAlignLeft);
        foreach (var item in items)
        {
            var line = $"{item.Product.Name} x{item.QuantityDisplay}";
            // No currency symbol. It was hardcoded to "$" on every line and total of
            // every sale, in stores that do not take dollars — and the return and
            // exchange receipts next to it have always printed the bare amount.
            var price = Money(item.LineTotal);
            WriteLine(ms, PadLine(line, price, 32), codePage);

            // A unit line prints both figures: the customer asked for square
            // metres and is billed for whole tiles, and showing only one of the
            // two makes the round-up look like an error.
            if (item.Product.HasSecondaryUnit)
                WriteLine(ms, $"    {item.QuantityInUnitDisplay} {item.Product.UnitShortName}", codePage);
        }
        WriteLine(ms, "----------------------------", codePage);
        WriteLine(ms, PadLine("Subtotal:", Money(subtotal), 32), codePage);
        if (discount > 0)
        {
            WriteLine(ms, PadLine("Discount:", $"-{Money(discount)}", 32), codePage);
            if (!string.IsNullOrWhiteSpace(discountName))
                WriteLine(ms, Truncate(discountName!, 32), codePage);
        }

        Write(ms, CmdBoldOn);
        WriteLine(ms, PadLine("TOTAL:", Money(total), 32), codePage);
        Write(ms, CmdBoldOff);
        WriteLine(ms, "----------------------------", codePage);
        Write(ms, CmdAlignCenter);
        WriteLine(ms, "Thank you for shopping!", codePage);
        Write(ms, CmdLineFeed);
        Write(ms, CmdLineFeed);
        Write(ms, CmdCut);
        return ms.ToArray();
    }

    /// <summary>Образец, по которому на точке решают, угадана ли таблица.
    ///
    /// Не «Hello world»: проверять надо ровно то, что ломалось. Русская строка —
    /// собственно проверка; строка таджикских и казахских букв напечатается
    /// вопросительными знаками при ЛЮБОЙ записи каталога, и это ожидаемо —
    /// однобайтовой таблицы под них у ESC/POS нет. Она стоит здесь, чтобы это
    /// увидели на бумаге, а не на названиях товаров через неделю. Латиница и
    /// цифры отделяют «таблица не та» от «принтер вообще не тот».</summary>
    public static byte[] BuildTestReceipt(EscPosCodePage codePage)
    {
        using var ms = new MemoryStream();
        WriteInit(ms, codePage);
        Write(ms, CmdAlignCenter);
        Write(ms, CmdBoldOn);
        WriteLine(ms, "ПРОБНАЯ ПЕЧАТЬ", codePage);
        Write(ms, CmdBoldOff);
        WriteLine(ms, "--------------------------------", codePage);
        Write(ms, CmdAlignLeft);
        WriteLine(ms, "RU: Ёжик съел 12 шт.", codePage);
        WriteLine(ms, "TJ/KK: ӯ ғ қ ҳ ҷ ә ң ө ұ ү і", codePage);
        WriteLine(ms, "LAT: The quick brown fox", codePage);
        WriteLine(ms, "NUM: 0123456789", codePage);
        WriteLine(ms, "--------------------------------", codePage);
        // Что именно пробовали — чтобы точка могла назвать это по телефону, не
        // залезая в настройки.
        WriteLine(ms, $"{codePage.Id}   ESC t {codePage.EscTSelector}", codePage);
        Write(ms, CmdLineFeed);
        Write(ms, CmdLineFeed);
        Write(ms, CmdCut);
        return ms.ToArray();
    }

    /// <summary>Отправляет <see cref="BuildTestReceipt"/> и не глотает отказ:
    /// кнопке проверки нужен не bool, а причина.</summary>
    public Task PrintTestReceiptAsync() => SendAsync(BuildTestReceipt(_codePage));

    public async Task<bool> PrintReceiptAsync(IEnumerable<CartItem> items, decimal subtotal, decimal discount, decimal total, IEnumerable<Coupon> coupons, string? discountName = null,
        string? documentNumber = null, string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        try
        {
            await SendAsync(BuildSaleReceipt(_codePage, items, subtotal, discount, total, discountName,
                documentNumber, warehouseName, sellerName, saleDate));
            // Обратный переход: без него первый же отказ красит индикатор
            // навсегда, потому что SetStatus нигде не вызывался с Ready.
            SetStatus(PrinterStatus.Ready);
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
            WriteInit(ms, _codePage);
            Write(ms, CmdAlignCenter);
            WriteLine(ms, "PRE-RECEIPT", _codePage);
            WriteLine(ms, "----------------------------", _codePage);
            Write(ms, CmdAlignLeft);
            foreach (var item in items)
            {
                WriteLine(ms, $"  {item.Product.Name} x{item.QuantityDisplay}", _codePage);
                if (item.Product.HasSecondaryUnit)
                    WriteLine(ms, $"    {item.QuantityInUnitDisplay} {item.Product.UnitShortName}", _codePage);
            }
            WriteLine(ms, PadLine("TOTAL:", Money(total), 32), _codePage);
            Write(ms, CmdLineFeed);
            Write(ms, CmdCut);
            await SendAsync(ms.ToArray());
            SetStatus(PrinterStatus.Ready);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Pre-receipt print error: {ex.Message}");
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }

    private static void Write(MemoryStream ms, byte[] data) => ms.Write(data, 0, data.Length);

    /// <summary>ESC @ и следом ESC t n. Одним методом, а не двумя командами по
    /// месту: CmdInit пишется в четырёх местах — три билдера и PrintPreReceiptAsync,
    /// который собирает буфер мимо них, — и дописывать выбор таблицы руками рядом с
    /// каждым значит однажды пропустить четвёртый. Пречек за смену печатается чаще
    /// всех прочих чеков вместе взятых, и его молчаливый откат на дефолтную таблицу
    /// выглядел бы как «иногда печатает мусор».</summary>
    private static void WriteInit(MemoryStream ms, EscPosCodePage codePage)
    {
        Write(ms, CmdInit);
        ms.WriteByte(0x1B);
        ms.WriteByte(0x74);
        ms.WriteByte(codePage.EscTSelector);
    }

    private static void WriteLine(MemoryStream ms, string text, EscPosCodePage codePage)
    {
        var bytes = codePage.Encoding.GetBytes(text + "\n");
        ms.Write(bytes, 0, bytes.Length);
    }
    /// <summary>Amounts on a receipt, formatted the same way on every register.
    /// Interpolating with ":F2" took the decimal separator from the operating
    /// system's locale, so the same sale printed 20.00 on one till and 20,00 on the
    /// next — and CartItem.QuantityDisplay, right beside it on the line, has always
    /// used the invariant form.</summary>
    private static string Money(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

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
        // Проект таргетит net10.0, не net10.0-windows, поэтому платформенная
        // проверка обязательна. OperatingSystem.IsWindows(), а не
        // RuntimeInformation: CA1416 распознаёт её как platform guard
        // гарантированно.
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "USB printing goes through the Windows spooler and is unavailable on this OS.");
        }

        return SendViaSpoolerAsync(_connectionString, data);
    }

    /// <summary>Отдельным методом с атрибутом, а не лямбдой на месте: guard из
    /// SendViaUsb не протекает в тело лямбды, и CA1416 сработал бы на ней.
    ///
    /// Task.Run, а не синхронный вызов: OpenPrinter и StartDocPrinter — это RPC
    /// в spoolsv.exe, и на зависшем спулере они блокируются на секунды. Без него
    /// весь цикл проходил бы на UI-потоке — SendViaUsb никогда не уступал поток,
    /// а CompositePrinterService строит список задач энергичным Select. Касса
    /// замерзала бы ровно в момент закрытия продажи.</summary>
    [SupportedOSPlatform("windows")]
    private static Task SendViaSpoolerAsync(string queueName, byte[] data)
        => Task.Run(() => WindowsRawPrinter.Send(queueName, data));

    private void SetStatus(PrinterStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    public static byte[] BuildReturnReceipt(
        EscPosCodePage codePage,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> lines,
        decimal totalRefund, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        using var ms = new MemoryStream();
        WriteInit(ms, codePage);
        Write(ms, CmdAlignCenter);
        Write(ms, CmdDoubleSizeOn);
        WriteLine(ms, "RETURN / VOZVRAT", codePage);
        Write(ms, CmdDoubleSizeOff);
        WriteLine(ms, $"Doc #{documentNumber}", codePage);
        if (!string.IsNullOrWhiteSpace(saleDate)) WriteLine(ms, saleDate, codePage);
        if (!string.IsNullOrWhiteSpace(warehouseName)) WriteLine(ms, $"Whse: {warehouseName}", codePage);
        if (!string.IsNullOrWhiteSpace(sellerName)) WriteLine(ms, $"Seller: {sellerName}", codePage);
        WriteLine(ms, "----------------------------", codePage);
        Write(ms, CmdAlignLeft);
        foreach (var l in lines)
            WriteLine(ms, PadLine($"{l.Name} x{l.Quantity}", Money(l.LineRefund), 32), codePage);
        WriteLine(ms, "----------------------------", codePage);
        Write(ms, CmdBoldOn);
        WriteLine(ms, PadLine("REFUND:", Money(totalRefund), 32), codePage);
        Write(ms, CmdBoldOff);
        Write(ms, CmdLineFeed);
        Write(ms, CmdLineFeed);
        Write(ms, CmdCut);
        return ms.ToArray();
    }

    public async Task<bool> PrintReturnReceiptAsync(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> lines,
        decimal totalRefund, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        try
        {
            await SendAsync(BuildReturnReceipt(_codePage, lines, totalRefund, documentNumber, warehouseName, sellerName, saleDate));
            SetStatus(PrinterStatus.Ready);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Return receipt print error: {ex.Message}");
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }

    public async Task<bool> OpenCashDrawerAsync()
    {
        try
        {
            await SendAsync(CmdDrawerKick);
            SetStatus(PrinterStatus.Ready);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cash drawer error: {ex.Message}");
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
        EscPosCodePage codePage,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        using var ms = new MemoryStream();
        WriteInit(ms, codePage);
        Write(ms, CmdAlignCenter);
        Write(ms, CmdDoubleSizeOn);
        WriteLine(ms, "EXCHANGE / OBMEN", codePage);
        Write(ms, CmdDoubleSizeOff);
        WriteLine(ms, $"Doc #{documentNumber}", codePage);
        if (!string.IsNullOrWhiteSpace(saleDate)) WriteLine(ms, saleDate, codePage);
        if (!string.IsNullOrWhiteSpace(warehouseName)) WriteLine(ms, $"Whse: {warehouseName}", codePage);
        if (!string.IsNullOrWhiteSpace(sellerName)) WriteLine(ms, $"Seller: {sellerName}", codePage);
        WriteLine(ms, "----------------------------", codePage);
        Write(ms, CmdAlignLeft);

        WriteLine(ms, "RETURNED:", codePage);
        foreach (var l in returned)
            WriteLine(ms, PadLine($"{l.Name} x{l.Quantity}", Money(l.LineRefund), 32), codePage);

        WriteLine(ms, "ISSUED:", codePage);
        foreach (var l in issued)
            WriteLine(ms, PadLine($"{l.Name} x{l.Quantity}", Money(l.LineRefund), 32), codePage);

        WriteLine(ms, "----------------------------", codePage);
        Write(ms, CmdBoldOn);
        // An even swap owes nothing in either direction; without its own label it
        // printed "REFUND: 0.00" and invited the customer to ask for the money.
        var label = difference > 0 ? "AMOUNT DUE:" : difference < 0 ? "REFUND:" : "NO DIFFERENCE:";
        WriteLine(ms, PadLine(label, Money(Math.Abs(difference)), 32), codePage);
        Write(ms, CmdBoldOff);
        Write(ms, CmdLineFeed);
        Write(ms, CmdLineFeed);
        Write(ms, CmdCut);
        return ms.ToArray();
    }

    public async Task<bool> PrintExchangeReceiptAsync(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        try
        {
            await SendAsync(BuildExchangeReceipt(_codePage, returned, issued, difference, documentNumber, warehouseName, sellerName, saleDate));
            SetStatus(PrinterStatus.Ready);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exchange receipt print error: {ex.Message}");
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }
}
