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

    /// <summary>Derived from <see cref="ManifestUrl"/> rather than hardcoded, so a move
    /// of the manifest to a CDN only requires changing that one constant. The download
    /// host must match this: against an attacker who can rewrite the whole manifest
    /// this changes little, but it narrows the blast radius when write access to the
    /// manifest is broader than publish access to the download directory (a CI job, a
    /// CMS, a webhook that only that one file is exposed through).</summary>
    private static readonly string ManifestHost = new Uri(ManifestUrl).Host;

    private readonly HttpClient _httpClient;
    private readonly IAppVersionProvider _versionProvider;
    private readonly string _downloadDirectory;

    /// <summary>Serialises the whole body of <see cref="DownloadAsync"/>. Every call
    /// starts by wiping <see cref="_downloadDirectory"/>: without this, two overlapping
    /// calls — a double click, or a retry racing the previous attempt's own cleanup —
    /// could have the second delete the first's fully downloaded, hash-verified file
    /// out from under it. Windows file locking keeps that from corrupting anything, but
    /// the cashier would see an unexplained failure on what should have succeeded.</summary>
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

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

    private static UpdateInfo? Parse(string json)
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
            if (!string.Equals(uri.Host, ManifestHost, StringComparison.OrdinalIgnoreCase)) return null;

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

    public async Task<string?> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        await _downloadLock.WaitAsync(ct);
        try
        {
            return await DownloadCoreAsync(info, progress, ct);
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private async Task<string?> DownloadCoreAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        // Clear leftovers here rather than in CheckAsync: the check runs on a timer, and
        // clearing there could delete a download the cashier started minutes ago.
        ClearDownloadDirectory();
        Directory.CreateDirectory(_downloadDirectory);

        // An unpredictable name rather than a fixed one: the installer runs elevated
        // (PrivilegesRequired=admin), so between the hash check below passing and the
        // caller handing this path to Process.Start, a non-elevated process running as
        // the same user could otherwise sit on a known path and swap the file in —
        // turning a same-user foothold into an elevated one. A random name means an
        // attacker has no path to pre-plant a race against. It narrows that window, it
        // does not close it; closing it fully would mean holding a file handle open
        // across the API boundary to the caller, which was judged not worth the
        // complexity here. ClearDownloadDirectory above still wipes the whole directory
        // on every attempt, so these random names do not accumulate on disk.
        var target = Path.Combine(_downloadDirectory, $"VvCashInstaller-{Guid.NewGuid():N}.exe");
        try
        {
            using (var response = await _httpClient.GetAsync(
                       info.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine(
                        $"[UpdateService] Download failed: HTTP {(int)response.StatusCode} {response.StatusCode}");
                    return null;
                }

                var total = response.Content.Headers.ContentLength ?? info.SizeBytes;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var destination = File.Create(target);

                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                    // total can be stale (a manifest sizeBytes that lags the published
                    // artifact) or absent from the response (a CDN answering chunked
                    // with no Content-Length, falling back to that same sizeBytes).
                    // Clamp so a bound Avalonia ProgressBar never receives a value past
                    // its Maximum.
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)written / total));
                }
            }

            var actualHash = await HashOfAsync(target, ct);
            if (!string.Equals(actualHash, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // The security-relevant event: an artifact fetched over an HTTPS
                // connection to the pinned host does not match what the manifest
                // published. Logged distinctly, with both hashes, so this is
                // identifiable after the fact and not indistinguishable from a
                // dropped connection or a full disk.
                Console.WriteLine(
                    $"[UpdateService] Hash mismatch: expected {info.Sha256}, got {actualHash}. Discarding download.");
                TryDelete(target);
                return null;
            }

            return target;
        }
        catch (OperationCanceledException)
        {
            // The ordinary case: the cashier pressed Cancel. Not a failure worth
            // alarming over, but the partial file still must not survive.
            Console.WriteLine("[UpdateService] Download cancelled.");
            TryDelete(target);
            return null;
        }
        catch (Exception ex)
        {
            // Connection dropped, disk full, a file that HashOfAsync could not open —
            // something unexpected. A partially written installer must never survive:
            // the next attempt starts clean.
            Console.WriteLine($"[UpdateService] Download failed: {ex.GetType().Name}: {ex.Message}");
            TryDelete(target);
            return null;
        }
    }

    private static async Task<string> HashOfAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void ClearDownloadDirectory()
    {
        try
        {
            if (Directory.Exists(_downloadDirectory)) Directory.Delete(_downloadDirectory, recursive: true);
        }
        catch
        {
            // A file still held open by a previous run is not worth failing over; the
            // download below overwrites what it needs.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort. The hash check is the guard that matters, and it already said no.
        }
    }
}
