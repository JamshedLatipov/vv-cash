using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Hardware;

public class CompositePrinterService : IPrinterService
{
    private readonly ISettingsService _settingsService;

    /// <summary>volatile, а не просто ссылка: присваивание ссылки атомарно (ECMA-335),
    /// но атомарность — не то же самое, что видимость. Атомарности хватает, чтобы не
    /// увидеть полусобранный список; чтобы гарантированно увидеть новый — нет.</summary>
    private volatile IReadOnlyList<EscPosPrinterService> _printers = Array.Empty<EscPosPrinterService>();

    /// <summary>Пересборки сериализуются между собой. Печать этот замок не трогает
    /// вовсе — внутри нет ни одного await, — так что прежнее рассуждение про
    /// «не держать мьютекс на сетевом вводе-выводе» в силе.
    ///
    /// Без него два наложившихся SettingsChanged расходятся так: оба отписывают,
    /// оба собирают, оба публикуют — и принтеры проигравшего остаются подписанными
    /// на недостижимом списке, продолжая перекрашивать индикатор до конца жизни
    /// процесса. Сегодня наложиться они не могут, потому что Save() всегда
    /// достигается на UI-потоке, но это свойство четырёх чужих файлов, а не этого.</summary>
    private readonly object _rebuildGate = new();

    private readonly Func<PrinterConfig, EscPosPrinterService> _factory;

    private PrinterStatus _overallStatus = PrinterStatus.Ready;

    public PrinterStatus Status => _overallStatus;
    public event EventHandler<PrinterStatus>? StatusChanged;

    /// <summary>Снимок текущего состава — только для чтения и только для тестов.
    /// Строка, которая доносит кодовую страницу из настроек до принтера, иначе не
    /// покрывается ничем, и её пропажу ловил бы только grep.</summary>
    internal IReadOnlyList<EscPosPrinterService> Printers => _printers;

    /// <summary>Фабрика существует ради проверки маршрутизации: без неё состав
    /// принтеров создаётся внутри и подменить его нечем. По умолчанию — обычное
    /// создание, боевой путь тот же, что был.</summary>
    public CompositePrinterService(ISettingsService settingsService,
        Func<PrinterConfig, EscPosPrinterService>? printerFactory = null)
    {
        _settingsService = settingsService;
        _factory = printerFactory ?? (config => new EscPosPrinterService(
            config.ConnectionType, config.ConnectionString,
            EscPosCodePages.Resolve(config.CodePageId), config.Roles));
        _settingsService.SettingsChanged += OnSettingsChanged;
        InitializePrinters();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        InitializePrinters();
    }

    /// <summary>Собирает новый список и присваивает его одним движением, вместо того
    /// чтобы править существующий на месте. Сама пересборка сериализована через
    /// _rebuildGate; печать в этот замок не заходит вовсе — почему это безопасно,
    /// см. его комментарий, — так что подвесить экран настроек на время печати он
    /// не может.
    ///
    /// Печать, начатая до смены настроек, доводится до конца на прежнем составе.
    /// Если она упадёт ПОСЛЕ подмены, её StatusChanged уже некому услышать и общий
    /// Status останется Ready — возвращаемый bool при этом честный, расходится
    /// только индикатор. Принято сознательно: держать подписки на выброшенных
    /// принтерах до конца их последней задачи стоит заметно больше механики, чем
    /// расхождение индикатора на одну печать. Поэтому отписка — последним шагом
    /// внутри лока, после публикации, а не первым: раньше она делала бы глухим уже
    /// момент начала пересборки, а не момент подмены.</summary>
    private void InitializePrinters()
    {
        lock (_rebuildGate)
        {
            var previous = _printers;

            var rebuilt = new List<EscPosPrinterService>();
            var configs = _settingsService.Printers?.Where(p => p.IsEnabled);
            if (configs != null)
            {
                foreach (var config in configs)
                {
                    var printer = _factory(config);
                    printer.StatusChanged += OnPrinterStatusChanged;
                    rebuilt.Add(printer);
                }
            }

            _printers = rebuilt;

            // Отписка ПОСЛЕ публикации: иначе печать, стартовавшая между отпиской и
            // присваиванием, получает прежний список с уже сорванными подписками —
            // окно глухоты шире, чем «упала после подмены».
            foreach (var printer in previous)
            {
                printer.StatusChanged -= OnPrinterStatusChanged;
            }
        }

        UpdateOverallStatus();
    }

    private void OnPrinterStatusChanged(object? sender, PrinterStatus e)
    {
        UpdateOverallStatus();
    }

    private void UpdateOverallStatus()
    {
        var printers = _printers;
        if (printers.Count == 0)
        {
            SetStatus(PrinterStatus.Ready);
            return;
        }

        // If any printer is in error, report error. If any is out of paper, report no paper.
        // Otherwise, report ready.
        if (printers.Any(p => p.Status == PrinterStatus.Error))
        {
            SetStatus(PrinterStatus.Error);
        }
        else if (printers.Any(p => p.Status == PrinterStatus.NoPaper))
        {
            SetStatus(PrinterStatus.NoPaper);
        }
        else if (printers.Any(p => p.Status == PrinterStatus.Offline))
        {
            SetStatus(PrinterStatus.Offline);
        }
        else
        {
            SetStatus(PrinterStatus.Ready);
        }
    }

    private void SetStatus(PrinterStatus status)
    {
        if (_overallStatus != status)
        {
            _overallStatus = status;
            StatusChanged?.Invoke(this, status);
        }
    }

    /// <summary>Состав под конкретный документ. Пустой список означает «на этой
    /// точке такой документ не печатают» — законная настройка, поэтому вызывающие
    /// возвращают false, а не бросают.</summary>
    private IReadOnlyList<EscPosPrinterService> For(PrintRole role)
        => _printers.Where(p => p.Roles.HasFlag(role)).ToList();

    public async Task<bool> PrintReceiptAsync(IEnumerable<CartItem> items, decimal subtotal, decimal discount, decimal total, IEnumerable<Coupon> coupons, string? discountName = null,
        string? documentNumber = null, string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        var printers = For(PrintRole.Receipt);
        if (printers.Count == 0)
        {
            return false; // Or true if we consider "no printers configured" as success?
        }

        var tasks = printers.Select(p => p.PrintReceiptAsync(items, subtotal, discount, total, coupons, discountName,
            documentNumber, warehouseName, sellerName, saleDate)).ToList();
        await Task.WhenAll(tasks);

        // Return true if at least one printer succeeded
        return tasks.Any(t => t.Result);
    }

    public async Task<bool> PrintTicketAsync(string number, string? time = null, string? warehouseName = null)
    {
        var printers = For(PrintRole.Ticket);
        if (printers.Count == 0) return false;
        var tasks = printers.Select(p => p.PrintTicketAsync(number, time, warehouseName)).ToList();
        await Task.WhenAll(tasks);
        return tasks.Any(t => t.Result);
    }

    public async Task<bool> PrintKitchenOrderAsync(SaleReceiptData sale, string queueNumber)
    {
        var printers = For(PrintRole.KitchenOrder);
        if (printers.Count == 0) return false;
        var tasks = printers.Select(p => p.PrintKitchenOrderAsync(sale, queueNumber)).ToList();
        await Task.WhenAll(tasks);
        return tasks.Any(t => t.Result);
    }

    public async Task<bool> PrintPreReceiptAsync(IEnumerable<CartItem> items, decimal total)
    {
        var printers = _printers;
        if (printers.Count == 0)
        {
            return false;
        }

        var tasks = printers.Select(p => p.PrintPreReceiptAsync(items, total)).ToList();
        await Task.WhenAll(tasks);

        return tasks.Any(t => t.Result);
    }

    public async Task<bool> OpenCashDrawerAsync()
    {
        var printers = _printers;
        if (printers.Count == 0) return false;
        var tasks = printers.Select(p => p.OpenCashDrawerAsync()).ToList();
        await Task.WhenAll(tasks);
        return tasks.Any(t => t.Result);
    }

    public async Task<bool> PrintReturnReceiptAsync(
        IEnumerable<VvCash.Models.ReturnReceiptLine> lines, decimal totalRefund, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        var printers = _printers;
        if (printers.Count == 0) return false;
        var list = lines.ToList();
        var tasks = printers.Select(p => p.PrintReturnReceiptAsync(list, totalRefund, documentNumber, warehouseName, sellerName, saleDate)).ToList();
        await Task.WhenAll(tasks);
        return tasks.Any(t => t.Result);
    }

    public async Task<bool> PrintExchangeReceiptAsync(
        IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        var printers = _printers;
        if (printers.Count == 0) return false;
        var returnedList = returned.ToList();
        var issuedList = issued.ToList();
        var tasks = printers.Select(p => p.PrintExchangeReceiptAsync(returnedList, issuedList, difference, documentNumber, warehouseName, sellerName, saleDate)).ToList();
        await Task.WhenAll(tasks);
        return tasks.Any(t => t.Result);
    }
}
