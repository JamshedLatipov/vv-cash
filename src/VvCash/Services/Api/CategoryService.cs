using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Data;

namespace VvCash.Services.Api;

public class CategoryService : ICategoryService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly IOfflineStorageService _storageService;

    public CategoryService(HttpClient httpClient, ISettingsService settingsService, IOfflineStorageService storageService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _storageService = storageService;
    }

    private async Task<IEnumerable<Category>> FetchPaginatedAsync(string endpoint)
    {
        var allCategories = new List<Category>();
        bool isSuccess = true;

        try
        {
            var baseUrl = _settingsService.BackendUrl;
            if (string.IsNullOrWhiteSpace(baseUrl)) return allCategories;
            if (!baseUrl.EndsWith("/")) baseUrl += "/";

            int currentPage = 1;
            int totalPages = 1;

            do
            {
                var cleanEndpoint = endpoint.TrimEnd('/');
                var url = $"{baseUrl}{cleanEndpoint}/?page={currentPage}";
                var response = await _httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    using var jsonDoc = JsonDocument.Parse(content);
                    var root = jsonDoc.RootElement;

                    if (root.TryGetProperty("page_count", out var pageCountElement) && pageCountElement.ValueKind == JsonValueKind.Number)
                    {
                        totalPages = pageCountElement.GetInt32();
                    }
                    else
                    {
                        totalPages = 1;
                    }

                    if (root.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.Array)
                    {
                        var pageItems = JsonSerializer.Deserialize<List<Category>>(bodyElement.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (pageItems != null)
                        {
                            foreach (var cat in pageItems)
                            {
                                if (cat.Image?.Path != null)
                                {
                                    var uri = new Uri(baseUrl);
                                    var origin = $"{uri.Scheme}://{uri.Authority}";
                                    cat.ImageUrl = $"{origin}/{cat.Image.Path.TrimStart('/')}";
                                    Debug.WriteLine($"[CategoryService] Image URL for '{cat.Name}': {cat.ImageUrl}");
                                }
                            }
                            allCategories.AddRange(pageItems);
                        }
                    }
                }
                else
                {
                    isSuccess = false;
                    break;
                }

                currentPage++;
            } while (currentPage <= totalPages);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CategoryService] Error fetching categories from {endpoint}: {ex.Message}");
            isSuccess = false;
        }

        if (isSuccess && allCategories.Any())
        {
            if (endpoint.Contains("show-on-cash"))
            {
                await _storageService.SaveQuickAccessCategoriesAsync(allCategories);
            }
            else
            {
                await _storageService.SaveCategoriesAsync(allCategories);
            }
        }
        else
        {
            // Fallback to local storage
            if (endpoint.Contains("show-on-cash"))
            {
                var cached = await _storageService.GetQuickAccessCategoriesAsync();
                if (cached != null && cached.Any())
                {
                    allCategories = cached.ToList();
                }
            }
            else
            {
                var cached = await _storageService.GetCategoriesAsync();
                if (cached != null && cached.Any())
                {
                    allCategories = cached.ToList();
                }
            }
        }

        return allCategories;
    }

    public Task<IEnumerable<Category>> GetCategoriesAsync()
    {
        return FetchPaginatedAsync("cashes/category");
    }

    public Task<IEnumerable<Category>> GetQuickAccessCategoriesAsync()
    {
        return FetchPaginatedAsync("cashes/category/show-on-cash");
    }
}
