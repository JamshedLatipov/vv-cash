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

    public SettingsService()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDir = Path.Combine(appDataPath, "VvCash");
        Directory.CreateDirectory(appDir);
        _settingsFilePath = Path.Combine(appDir, "settings.json");

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
            catch
            {
                _data = new SettingsData();
            }
        }
        else
        {
            _data = new SettingsData();
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
        catch (Exception)
        {
            // Log exception here
        }
    }
}
