using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VvCash.Services;

public partial class I18nService : ObservableObject
{
    private static readonly I18nService _instance = new();
    public static I18nService Instance => _instance;

    private Dictionary<string, string> _currentStrings = new();

    [ObservableProperty]
    private string _currentLanguage = "ru";

    private I18nService()
    {
    }

    public void Initialize(string languageCode)
    {
        if (CurrentLanguage == languageCode)
        {
            LoadLanguage(languageCode);
            OnPropertyChanged("Item");
        }
        else
        {
            CurrentLanguage = languageCode;
        }
    }

    partial void OnCurrentLanguageChanged(string value)
    {
        LoadLanguage(value);
        OnPropertyChanged("Item");
    }

    private void LoadLanguage(string lang)
    {
        try
        {
            var uri = new Uri($"avares://VvCash/Assets/i18n/{lang}.json");
            using var stream = AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            _currentStrings = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (Exception)
        {
            _currentStrings = new Dictionary<string, string>();
        }
    }

    public string this[string key]
    {
        get
        {
            if (_currentStrings.TryGetValue(key, out var val))
                return val;
            return $"[{key}]";
        }
    }
}
