using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using VvCash.Models;

namespace VvCash.Services;

public class SettingsData
{
    public string BackendUrl { get; set; } = string.Empty;
    public string CashRegisterToken { get; set; } = string.Empty;
    public string AuthToken { get; set; } = string.Empty;
    public DateTime? AuthTokenExpiresAt { get; set; }
    public int SyncIntervalMinutes { get; set; } = 10;
    public string Language { get; set; } = "ru";
    public List<PrinterConfig> Printers { get; set; } = new();
    public bool ReturnOpenCashDrawer { get; set; } = true;
    public bool ReturnPrintReceipt { get; set; } = true;
    public string ExchangePayoutCategoryId { get; set; } = string.Empty;
    public string ReturnPayoutCategoryId { get; set; } = string.Empty;
    public string PhoneFormatId { get; set; } = string.Empty;
}

public class SettingsService : ISettingsService
{
    private readonly string _settingsFilePath;
    private SettingsData _data = new SettingsData();

    public event EventHandler? SettingsChanged;

    public string BackendUrl
    {
        get => _data.BackendUrl;
        set => _data.BackendUrl = value;
    }

    public string CashRegisterToken
    {
        get => _data.CashRegisterToken;
        set => _data.CashRegisterToken = value;
    }

    public string AuthToken
    {
        get => _data.AuthToken;
        set => _data.AuthToken = value;
    }

    public DateTime? AuthTokenExpiresAt
    {
        get => _data.AuthTokenExpiresAt;
        set => _data.AuthTokenExpiresAt = value;
    }

    public int SyncIntervalMinutes
    {
        get => _data.SyncIntervalMinutes <= 0 ? 10 : _data.SyncIntervalMinutes;
        set => _data.SyncIntervalMinutes = value;
    }

    public string Language
    {
        get => _data.Language;
        set => _data.Language = value;
    }

    public List<PrinterConfig> Printers
    {
        get => _data.Printers;
        set => _data.Printers = value ?? new List<PrinterConfig>();
    }

    public bool ReturnOpenCashDrawer
    {
        get => _data.ReturnOpenCashDrawer;
        set => _data.ReturnOpenCashDrawer = value;
    }

    public bool ReturnPrintReceipt
    {
        get => _data.ReturnPrintReceipt;
        set => _data.ReturnPrintReceipt = value;
    }

    public string ExchangePayoutCategoryId
    {
        get => _data.ExchangePayoutCategoryId;
        set => _data.ExchangePayoutCategoryId = value ?? string.Empty;
    }

    public string ReturnPayoutCategoryId
    {
        get => _data.ReturnPayoutCategoryId;
        set => _data.ReturnPayoutCategoryId = value ?? string.Empty;
    }

    public string PhoneFormatId
    {
        get => _data.PhoneFormatId;
        set => _data.PhoneFormatId = value;
    }

    /// <summary>Creates the service against the standard per-user settings file. Pass
    /// <paramref name="settingsFilePath"/> to point at a different one (e.g. a temp file
    /// in tests); left null/empty, DI and production code get the usual
    /// LocalApplicationData path unchanged — same arrangement as OfflineStorageService.</summary>
    public SettingsService(string? settingsFilePath = null)
    {
        if (string.IsNullOrEmpty(settingsFilePath))
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appDir = Path.Combine(appDataPath, "VvCash");
            Directory.CreateDirectory(appDir);
            settingsFilePath = Path.Combine(appDir, "settings.json");
        }
        _settingsFilePath = settingsFilePath;

        Load();
    }

    private void Load()
    {
        if (File.Exists(_settingsFilePath))
        {
            try
            {
                var json = File.ReadAllText(_settingsFilePath);
                _data = JsonSerializer.Deserialize<SettingsData>(json) ?? new SettingsData();
                if (_data.SyncIntervalMinutes <= 0)
                {
                    _data.SyncIntervalMinutes = 10;
                }
                if (string.IsNullOrEmpty(_data.Language))
                {
                    _data.Language = "ru";
                }
                if (_data.Printers == null)
                {
                    _data.Printers = new List<PrinterConfig>();
                }
                if (_data.ExchangePayoutCategoryId == null)
                {
                    _data.ExchangePayoutCategoryId = string.Empty;
                }
                if (_data.ReturnPayoutCategoryId == null)
                {
                    _data.ReturnPayoutCategoryId = string.Empty;
                }
                if (_data.PhoneFormatId == null)
                {
                    _data.PhoneFormatId = string.Empty;
                }
            }
            catch (Exception ex)
            {
                // Defaults, so the register still starts — but not before the file that
                // could not be read is put somewhere it will survive. The first Save
                // after this overwrites settings.json, and what it overwrites used to be
                // the only copy of the backend URL, the cash token and the printer
                // configuration for this terminal. A register coming up blank is bad; a
                // register coming up blank with nothing left to recover from is worse.
                Console.WriteLine($"[SettingsService] Could not read settings: {ex.GetType().Name}: {ex.Message}");
                KeepCorruptFileAside();
                _data = new SettingsData();
            }
        }
        else
        {
            _data = new SettingsData();
        }
    }

    private void KeepCorruptFileAside()
    {
        try
        {
            var kept = $"{_settingsFilePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(_settingsFilePath, kept, overwrite: true);
            Console.WriteLine($"[SettingsService] Unreadable settings kept at {kept}");
        }
        catch (Exception ex)
        {
            // Best effort — the register must still start.
            Console.WriteLine($"[SettingsService] Could not keep the unreadable settings file: {ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Swallowed on purpose — a settings screen that throws mid-save strands the
            // cashier — but no longer silently: a register whose settings never actually
            // persist looks identical to one that was never configured, and that is a
            // support call nobody can diagnose without this line.
            Console.WriteLine($"[SettingsService] Could not save settings: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
