using System;
using System.Collections.Generic;
using VvCash.Models;

namespace VvCash.Services;

public interface ISettingsService
{
    string BackendUrl { get; set; }
    string CashRegisterToken { get; set; }
    string AuthToken { get; set; }
    int SyncIntervalMinutes { get; set; }
    List<PrinterConfig> Printers { get; set; }

    event EventHandler? SettingsChanged;

    void Save();
}
