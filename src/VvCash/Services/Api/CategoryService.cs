using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Api;

public class CategoryService : ICategoryService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public CategoryService(HttpClient httpClient, ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
    }

    public async Task<IEnumerable<Category>> GetCategoriesAsync()
    {
        try
        {
            var baseUrl = _settingsService.BackendUrl;
            if (string.IsNullOrWhiteSpace(baseUrl)) return new List<Category>();
            if (!baseUrl.EndsWith("/")) baseUrl += "/";

            var url = $"{baseUrl}cashes/category/";
            var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(content);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.Array)
                {
                    return JsonSerializer.Deserialize<List<Category>>(bodyElement.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<Category>();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CategoryService] Error fetching categories: {ex.Message}");
        }

        return new List<Category>();
    }

    public async Task<IEnumerable<Category>> GetQuickAccessCategoriesAsync()
    {
        try
        {
            var baseUrl = _settingsService.BackendUrl;
            if (string.IsNullOrWhiteSpace(baseUrl)) return new List<Category>();
            if (!baseUrl.EndsWith("/")) baseUrl += "/";

            var url = $"{baseUrl}cashes/category/show-on-cash/";
            var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(content);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.Array)
                {
                    return JsonSerializer.Deserialize<List<Category>>(bodyElement.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<Category>();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CategoryService] Error fetching quick access categories: {ex.Message}");
        }

        return new List<Category>();
    }
}
