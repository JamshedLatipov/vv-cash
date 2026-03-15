using System;
using System.IO;
using System.Text.Json;

namespace VvCash.Services;

public class SettingsData
{
    public string BackendUrl { get; set; } = string.Empty;
    public string CashRegisterToken { get; set; } = string.Empty;
    public string AuthToken { get; set; } = string.Empty;
}

public class SettingsService : ISettingsService
{
    private readonly string _settingsFilePath;
    private SettingsData _data = new SettingsData();

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
        }
        catch (Exception)
        {
            // Log exception here
        }
    }
}
