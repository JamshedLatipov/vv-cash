using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VvCash.Services.Update;

public sealed class UpdateService : IUpdateService
{
    /// <summary>Published next to the installer and uploaded by the same hand. Not a
    /// setting: every register talks to the cloud, and a per-register update URL would
    /// be one more thing to get wrong on site.</summary>
    private const string ManifestUrl = "https://proffi.io/downloads/kassa-latest.json";

    private const string ProductId = "vvcash";

    private readonly HttpClient _httpClient;
    private readonly IAppVersionProvider _versionProvider;
    private readonly string _downloadDirectory;

    public UpdateService(
        HttpClient httpClient,
        IAppVersionProvider versionProvider,
        string? downloadDirectory = null)
    {
        _httpClient = httpClient;
        _versionProvider = versionProvider;
        _downloadDirectory = downloadDirectory
            ?? Path.Combine(Path.GetTempPath(), "VvCash", "updates");
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await _httpClient.GetAsync(ManifestUrl, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;

            // proffi.io serves a single-page app: a path it does not know answers 200
            // with index.html. The status code alone proves nothing.
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                return null;

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var info = Parse(body);
            if (info is null) return null;

            return info.Version > AppVersion.Normalize(_versionProvider.Current) ? info : null;
        }
        catch
        {
            // No network, DNS failure, timeout, torn connection. All the same to the
            // cashier: nothing appears, and the loop tries again in an hour.
            return null;
        }
    }

    internal static UpdateInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!TryGetString(root, "product", out var product) || product != ProductId) return null;

            if (!TryGetString(root, "version", out var versionText)) return null;
            if (!Version.TryParse(versionText, out var version)) return null;

            if (!TryGetString(root, "url", out var url)) return null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttps) return null;

            if (!TryGetString(root, "sha256", out var sha256)) return null;
            if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) return null;

            long sizeBytes = root.TryGetProperty("sizeBytes", out var sizeElement)
                             && sizeElement.ValueKind == JsonValueKind.Number
                             && sizeElement.TryGetInt64(out var size)
                ? size
                : 0;

            string? notes = TryGetString(root, "notes", out var notesText) ? notesText : null;

            return new UpdateInfo(
                AppVersion.Normalize(version),
                uri.ToString(),
                sha256.ToLowerInvariant(),
                sizeBytes,
                notes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element)) return false;
        if (element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? string.Empty;
        return true;
    }

    public Task<string?> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
        => throw new NotImplementedException("Task 5");
}
