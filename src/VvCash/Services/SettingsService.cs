using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using VvCash.Models;
using VvCash.Services.Queue;

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
    public string CustomerDisplayPort { get; set; } = string.Empty;
    public int CustomerDisplayBaudRate { get; set; } = 9600;
    public string CustomerDisplayCodePageId { get; set; } = string.Empty;

    public QueueRole QueueRole { get; set; } = QueueRole.Off;
    public string QueueServerAddress { get; set; } = string.Empty;
    public int QueuePort { get; set; } = 8770;
    public string QueueSecret { get; set; } = string.Empty;
    public int TillIndex { get; set; }
}

public class SettingsService : ISettingsService, IQueueSettings
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

    public string CustomerDisplayPort
    {
        get => _data.CustomerDisplayPort;
        set => _data.CustomerDisplayPort = value;
    }

    /// <summary>Ноль и отрицательное читаются как 9600 — тем же приёмом, что
    /// SyncIntervalMinutes выше: settings.json правят руками.</summary>
    public int CustomerDisplayBaudRate
    {
        get => _data.CustomerDisplayBaudRate <= 0 ? 9600 : _data.CustomerDisplayBaudRate;
        set => _data.CustomerDisplayBaudRate = value;
    }

    public string CustomerDisplayCodePageId
    {
        get => _data.CustomerDisplayCodePageId;
        set => _data.CustomerDisplayCodePageId = value;
    }

    public QueueRole QueueRole
    {
        get => _data.QueueRole;
        set => _data.QueueRole = value;
    }

    public string QueueServerAddress
    {
        get => _data.QueueServerAddress;
        set => _data.QueueServerAddress = value ?? string.Empty;
    }

    /// <summary>Ноль и отрицательное читаются как 8770 — тем же приёмом, что
    /// SyncIntervalMinutes и CustomerDisplayBaudRate выше: settings.json правят
    /// руками.</summary>
    public int QueuePort
    {
        get => _data.QueuePort <= 0 ? 8770 : _data.QueuePort;
        set => _data.QueuePort = value;
    }

    public string QueueSecret
    {
        get => _data.QueueSecret;
        set => _data.QueueSecret = value ?? string.Empty;
    }

    /// <summary>Зажимается в 0..NumberPool.Tills-1, а не принимается как есть:
    /// значение из settings.json правится руками, и вне диапазона касса начнёт
    /// делить по чужому классу вычетов пула.</summary>
    public int TillIndex
    {
        get => Math.Clamp(_data.TillIndex, 0, NumberPool.Tills - 1);
        set => _data.TillIndex = value;
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
                if (_data.CustomerDisplayPort == null)
                {
                    _data.CustomerDisplayPort = string.Empty;
                }
                if (_data.CustomerDisplayBaudRate <= 0)
                {
                    _data.CustomerDisplayBaudRate = 9600;
                }
                if (_data.CustomerDisplayCodePageId == null)
                {
                    _data.CustomerDisplayCodePageId = string.Empty;
                }
                if (_data.QueueServerAddress == null)
                {
                    _data.QueueServerAddress = string.Empty;
                }
                if (_data.QueuePort <= 0)
                {
                    _data.QueuePort = 8770;
                }
                if (_data.QueueSecret == null)
                {
                    _data.QueueSecret = string.Empty;
                }
                _data.TillIndex = Math.Clamp(_data.TillIndex, 0, NumberPool.Tills - 1);
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
