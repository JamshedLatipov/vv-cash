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

    private async Task<IEnumerable<Category>> FetchPaginatedAsync(string endpoint)
    {
        var allCategories = new List<Category>();
        try
        {
            var baseUrl = _settingsService.BackendUrl;
            if (string.IsNullOrWhiteSpace(baseUrl)) return allCategories;
            if (!baseUrl.EndsWith("/")) baseUrl += "/";

            int currentPage = 1;
            int totalPages = 1;

            do
            {
                // Remove trailing slash from endpoint if it exists so we can safely add query params
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
                        // If there is no page_count, assume it's just a single page response and exit loop after this
                        totalPages = 1;
                    }

                    if (root.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.Array)
                    {
                        var pageItems = JsonSerializer.Deserialize<List<Category>>(bodyElement.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (pageItems != null)
                        {
                            allCategories.AddRange(pageItems);
                        }
                    }
                }
                else
                {
                    break;
                }

                currentPage++;
            } while (currentPage <= totalPages);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CategoryService] Error fetching categories from {endpoint}: {ex.Message}");
        }

        return allCategories;
    }

    public Task<IEnumerable<Category>> GetCategoriesAsync()
    {
        // the user's trace showed /products/category/ but swagger said /cashes/category/
        // I will use cashes/category/ as instructed initially, if it fails they might need to change it
        // Or wait, the user specifically pasted the trace for `http://market.proffi.io/api/v1/products/category/?page=1`!
        // The message was:
        // Request URL http://market.proffi.io/api/v1/products/category/?page=1
        // So I should probably use `products/category` for all categories, and `cashes/category/show-on-cash` for quick access.
        // Let's use what they pasted exactly!
        return FetchPaginatedAsync("cashes/category");
    }

    public Task<IEnumerable<Category>> GetQuickAccessCategoriesAsync()
    {
        return FetchPaginatedAsync("cashes/category/show-on-cash");
    }
}
