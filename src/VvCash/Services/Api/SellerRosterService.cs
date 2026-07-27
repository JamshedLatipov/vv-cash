using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Data;

namespace VvCash.Services.Api;

/// <summary>Loads the roster of sellers assigned to this cash register and caches it
/// locally, so seller switching and capability checks keep working with no network.</summary>
public class SellerRosterService : ISellerRosterService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly IOfflineStorageService _storage;

    // Coalesces concurrent RefreshAsync callers onto a single in-flight fetch.
    // RefreshAsync is genuinely called from more than one thread by design (the
    // Task 17 background sync loop calls it from a pool thread on its own cadence,
    // while the UI-thread shift-open/restore paths can call it around the same
    // moment), so without this, two overlapping HTTP round-trips race to write
    // SellerSession's roster last: whichever happens to resolve later wins,
    // regardless of which was actually more recent or more successful — a stale
    // cached-fallback response can silently overwrite a fresher server response.
    // Coalescing removes the ordering dependency for every caller instead of
    // requiring each call site to avoid overlapping on its own.
    //
    // Guarded by a real lock rather than a UI-thread assumption (unlike
    // SellerSession, see its own remarks) — this type's whole point is to be safe
    // off the UI thread. The lock's own critical sections (checking/assigning
    // _inFlightRefresh in RefreshAsync, checking/clearing it in
    // ClearInFlightIfCurrent) really are just a field read/write. But RunFetchAsync
    // is kicked off as a fire-and-forget call from *inside* RefreshAsync's lock
    // block, so C# runs it synchronously up to its first genuine suspension point
    // before that lock block returns — and when BackendUrl is blank, that path runs
    // straight into SafeGetCachedAsync's SQLite read via IOfflineStorageService.
    // ADO.NET's async calls typically complete synchronously for local SQLite (no
    // real thread switch), so in that case the lock is, in practice, still held
    // across that database round-trip, not just a field assignment. Re-entrant
    // Monitor locking means this can never deadlock the thread that already holds
    // it, but it does mean a second caller arriving on another thread can genuinely
    // block on the lock for the duration of that DB read.
    private readonly object _refreshLock = new();
    private Task<IEnumerable<SellerInfo>>? _inFlightRefresh;

    public SellerRosterService(HttpClient httpClient, ISettingsService settingsService, IOfflineStorageService storage)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _storage = storage;
    }

    // Deliberately a bare passthrough, per spec ("delegates straight to
    // storage.GetSellersAsync()") and unlike RefreshAsync, this method makes no
    // resilience promise in its contract - it is the on-demand cache read, not a
    // best-effort background refresh. Swallowing a storage failure here would
    // silently turn "the local database is broken" into "this register has no
    // sellers" for every caller, which is a worse outcome than letting the
    // exception surface to whoever is depending on the cache actually being
    // readable. RefreshAsync's own fallback path reads the cache defensively
    // (see SafeGetCachedAsync) precisely because IT must never throw; that
    // requirement does not extend to this method.
    public Task<IEnumerable<SellerInfo>> GetCachedAsync() => _storage.GetSellersAsync();

    /// <summary>Fetches the roster from the server and caches it. On any network or
    /// parse failure returns the cached roster instead, so the register keeps working.
    /// A caller arriving while a refresh is already in flight is handed that same
    /// in-flight task instead of starting a competing fetch (see <see cref="_inFlightRefresh"/>);
    /// a caller arriving after the previous refresh finished — successfully or not —
    /// always starts a fresh one.</summary>
    public Task<IEnumerable<SellerInfo>> RefreshAsync()
    {
        lock (_refreshLock)
        {
            if (_inFlightRefresh != null)
                return _inFlightRefresh;

            // Register the in-flight task via a TaskCompletionSource *before* starting
            // the actual fetch below, specifically so `_inFlightRefresh` is assigned
            // first and the fetch second. FetchRosterAsync can complete entirely
            // synchronously — an immediate throw (e.g. a settings accessor faulting) or
            // simply a handler that never truly suspends both count — and if the fetch
            // were started first with its result assigned to the field afterwards, a
            // synchronous completion would run RunFetchAsync's own cleanup (see
            // ClearInFlightIfCurrent below) before that later assignment executed,
            // leaving a stale reference cached forever. Setting the field to tcs.Task up
            // front means any cleanup that happens to run synchronously is clearing
            // exactly what was just set, never something written after it.
            var tcs = new TaskCompletionSource<IEnumerable<SellerInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlightRefresh = tcs.Task;
            _ = RunFetchAsync(tcs);
            return tcs.Task;
        }
    }

    // Drives the real fetch and always clears the in-flight slot once `tcs` is
    // completing — success or failure — so that by the time any awaiter of
    // RefreshAsync()'s returned task observes completion, the next call is already
    // free to start a fresh fetch rather than reuse this one. FetchRosterAsync
    // itself never throws by design (see its own try/catch), so the catch below is
    // defensive: it exists so that even an unanticipated failure (e.g. a settings
    // accessor throwing before FetchRosterAsync's own try block, as covered by
    // RefreshAsync_FailedSharedInFlightTask_DoesNotPoisonNextCall in the test suite)
    // cannot leave a faulted task cached for every subsequent caller to keep observing.
    // Whoever was awaiting this particular tcs.Task still sees that one failure — this
    // cannot un-fail a call already handed out — but the next call after it gets a
    // genuinely fresh attempt instead of a cached exception.
    //
    // Resolves `tcs` *before* clearing `_inFlightRefresh`, not after: clearing first
    // would open a window where a new caller takes `_refreshLock`, finds the slot
    // already empty, and starts a redundant fetch of its own instead of joining the
    // one that is about to finish anyway. Resolving first closes that window — any
    // caller that acquires the lock in between sees a task that is already complete
    // and simply awaits it instead of racing a fresh HTTP round-trip. This is safe
    // against re-entrant completion because the constructor uses
    // TaskCreationOptions.RunContinuationsAsynchronously: SetResult/SetException
    // never runs awaiters' continuations inline, so nothing downstream can observe
    // completion and re-enter this type before ClearInFlightIfCurrent below runs.
    private async Task RunFetchAsync(TaskCompletionSource<IEnumerable<SellerInfo>> tcs)
    {
        try
        {
            var result = await FetchRosterAsync();
            tcs.SetResult(result);
            ClearInFlightIfCurrent(tcs.Task);
        }
        catch (Exception ex)
        {
            tcs.SetException(ex);
            ClearInFlightIfCurrent(tcs.Task);
        }
    }

    private void ClearInFlightIfCurrent(Task<IEnumerable<SellerInfo>> task)
    {
        lock (_refreshLock)
        {
            if (ReferenceEquals(_inFlightRefresh, task))
                _inFlightRefresh = null;
        }
    }

    private async Task<IEnumerable<SellerInfo>> FetchRosterAsync()
    {
        Debug.WriteLine("[SellerRosterService] RefreshAsync called.");

        var baseUrl = _settingsService.BackendUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Debug.WriteLine("[SellerRosterService] BackendUrl is not configured; returning cache.");
            return await SafeGetCachedAsync();
        }

        if (!baseUrl.EndsWith("/"))
            baseUrl += "/";

        try
        {
            var url = $"{baseUrl}cashes/seller/";
            Debug.WriteLine($"[SellerRosterService] GET {url}");
            var response = await _httpClient.GetAsync(url);
            Debug.WriteLine($"[SellerRosterService] Response status: {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine("[SellerRosterService] Non-success status; returning cache.");
                return await SafeGetCachedAsync();
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            using var jsonDoc = JsonDocument.Parse(responseContent);
            var root = jsonDoc.RootElement;

            if (!root.TryGetProperty("status", out var statusElement) || statusElement.GetInt32() != 0)
            {
                Debug.WriteLine("[SellerRosterService] Envelope status is not 0; returning cache.");
                return await SafeGetCachedAsync();
            }

            if (!root.TryGetProperty("body", out var bodyElement) || bodyElement.ValueKind != JsonValueKind.Array)
            {
                Debug.WriteLine("[SellerRosterService] body is missing or not an array; returning cache.");
                return await SafeGetCachedAsync();
            }

            var sellers = bodyElement.Deserialize<List<SellerInfo>>() ?? new List<SellerInfo>();
            await _storage.SaveSellersAsync(sellers);
            return sellers;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SellerRosterService] Error refreshing seller roster: {ex.Message}");
            return await SafeGetCachedAsync();
        }
    }

    /// <summary>Reads the cache defensively: this is RefreshAsync's last line of
    /// defence, so it must not itself throw. If even the cache read fails (e.g. a
    /// locked or corrupt local database), an unhandled exception here would be
    /// strictly worse than "nothing changed" - it would propagate into whatever
    /// is calling RefreshAsync (a sync loop, a UI action) instead of degrading
    /// gracefully. Callers of the roster already treat an empty roster as a valid
    /// state (the design falls back to crediting the shift owner), so an empty
    /// enumerable is the correct terminal fallback.</summary>
    private async Task<IEnumerable<SellerInfo>> SafeGetCachedAsync()
    {
        try
        {
            return await _storage.GetSellersAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SellerRosterService] Error reading cached seller roster: {ex.Message}");
            return Array.Empty<SellerInfo>();
        }
    }
}
