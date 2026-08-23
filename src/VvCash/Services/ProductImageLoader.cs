using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using VvCash.Models;

namespace VvCash.Services;

/// <summary>Fetches product thumbnails off the backend and hands out one decoded
/// <see cref="Bitmap"/> per image path.
///
/// Shared rather than per-screen because the same product shows up in the catalog grid,
/// in the cart, and in both of the exchange screen's baskets; without a cache each of
/// those pulls the same jpeg down again, which a register on a shop's wifi pays for.
/// What is cached is the in-flight Task itself, so two screens asking at the same moment
/// share one round trip instead of racing two.
///
/// Static rather than an injected service on purpose: every call site already holds the
/// register's HttpClient and ISettingsService, and the cache has to be process-wide to be
/// worth having at all.</summary>
public static class ProductImageLoader
{
    /// <summary>Three hundred thumbnails. FetchAsync decodes to 256 pixels wide rather
    /// than native resolution (see the comment there for why that matters), so a
    /// roughly-square product photo costs about 256x256x4 = 256 KB decoded, holding the
    /// cache near seventy-five megabytes — affordable on a register, and comfortably
    /// more than one screenful of the grid, so scrolling back and forth does not evict
    /// what was just shown. Height is not independently capped: a source far taller than
    /// it is wide would decode taller than 256px too, but ordinary product photography
    /// does not do that.
    ///
    /// Bounded at all because a register runs for months without a restart and
    /// PosViewModel.Products is replaced wholesale on every category change: after that,
    /// the old Product objects are unreachable except through this cache, so an unbounded
    /// one pins every bitmap the shift ever displayed.</summary>
    private const int CacheCapacity = 300;

    private static readonly LruCache<string, Task<Bitmap?>> Cache = new(CacheCapacity);

    /// <summary>Null for a product with no image, an unreachable backend, or a fetch that
    /// failed — every caller reads "no bitmap" as "show the placeholder icon", so a
    /// missing image is never an error worth putting in front of a cashier.</summary>
    public static Task<Bitmap?> GetAsync(HttpClient? http, string? backendUrl, string? imagePath)
    {
        if (http == null || string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(backendUrl))
            return Task.FromResult<Bitmap?>(null);

        string url;
        try
        {
            var origin = new Uri(backendUrl);
            url = $"{origin.Scheme}://{origin.Authority}/{imagePath.TrimStart('/')}";
        }
        catch (UriFormatException)
        {
            return Task.FromResult<Bitmap?>(null);
        }

        var task = Cache.GetOrAdd(url, u => FetchAsync(http, u));
        if (task.IsCompletedSuccessfully && task.Result == null)
        {
            // A cached null is a failed attempt, and the usual reason is the register
            // being briefly offline. Caching that permanently would leave the product
            // iconless for the rest of the shift, so a later ask tries again.
            task = FetchAsync(http, url);
            Cache.Set(url, task);
        }
        return task;
    }

    /// <summary>The common case over <see cref="GetAsync"/>: stamp the bitmap onto the
    /// product itself. Posted to the UI thread because ImageBitmap is a bound observable
    /// property and the fetch finishes wherever the socket happens to complete.</summary>
    public static async Task LoadIntoAsync(HttpClient? http, string? backendUrl, Product product)
    {
        var bitmap = await GetAsync(http, backendUrl, product.ImagePath);
        if (bitmap == null) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => product.ImageBitmap = bitmap);
    }

    private static async Task<Bitmap?> FetchAsync(HttpClient http, string url)
    {
        try
        {
            var bytes = await http.GetByteArrayAsync(url);
            using var ms = new MemoryStream(bytes);
            // Decode to display width rather than native. The cap above counts entries, so
            // it only means anything if an entry has a bounded cost — and nothing upstream
            // bounds it: ImagePath prefers the full-size image and falls back to a thumb
            // only when there is none, so a phone photo arrives at its original pixels. The
            // grid draws these on a 182-wide card, so 256 is already generous.
            return Bitmap.DecodeToWidth(ms, 256);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProductImageLoader] {url}: {ex.Message}");
            return null;
        }
    }
}
