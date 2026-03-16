using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Hardware;

public class CompositePrinterService : IPrinterService
{
    private readonly ISettingsService _settingsService;
    private List<EscPosPrinterService> _printers = new();
    private PrinterStatus _overallStatus = PrinterStatus.Ready;

    public PrinterStatus Status => _overallStatus;
    public event EventHandler<PrinterStatus>? StatusChanged;

    public CompositePrinterService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        _settingsService.SettingsChanged += OnSettingsChanged;
        InitializePrinters();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        InitializePrinters();
    }

    private void InitializePrinters()
    {
        // Unsubscribe from existing printers
        foreach (var printer in _printers)
        {
            printer.StatusChanged -= OnPrinterStatusChanged;
        }

        _printers.Clear();

        var configs = _settingsService.Printers?.Where(p => p.IsEnabled).ToList();
        if (configs != null)
        {
            foreach (var config in configs)
            {
                var printer = new EscPosPrinterService(config.ConnectionType, config.ConnectionString);
                printer.StatusChanged += OnPrinterStatusChanged;
                _printers.Add(printer);
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
        if (!_printers.Any())
        {
            SetStatus(PrinterStatus.Ready);
            return;
        }

        // If any printer is in error, report error. If any is out of paper, report no paper.
        // Otherwise, report ready.
        if (_printers.Any(p => p.Status == PrinterStatus.Error))
        {
            SetStatus(PrinterStatus.Error);
        }
        else if (_printers.Any(p => p.Status == PrinterStatus.NoPaper))
        {
            SetStatus(PrinterStatus.NoPaper);
        }
        else if (_printers.Any(p => p.Status == PrinterStatus.Offline))
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

    public async Task<bool> PrintReceiptAsync(IEnumerable<CartItem> items, decimal subtotal, decimal tax, decimal discount, decimal total, IEnumerable<Coupon> coupons)
    {
        if (!_printers.Any())
        {
            return false; // Or true if we consider "no printers configured" as success?
        }

        var tasks = _printers.Select(p => p.PrintReceiptAsync(items, subtotal, tax, discount, total, coupons)).ToList();
        await Task.WhenAll(tasks);

        // Return true if at least one printer succeeded
        return tasks.Any(t => t.Result);
    }

    public async Task<bool> PrintPreReceiptAsync(IEnumerable<CartItem> items, decimal total)
    {
        if (!_printers.Any())
        {
            return false;
        }

        var tasks = _printers.Select(p => p.PrintPreReceiptAsync(items, total)).ToList();
        await Task.WhenAll(tasks);

        return tasks.Any(t => t.Result);
    }
}
