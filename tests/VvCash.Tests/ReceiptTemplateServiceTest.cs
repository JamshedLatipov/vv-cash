using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VvCash.Models;
using VvCash.Models.Receipt;
using VvCash.Services;
using VvCash.Services.Data;
using Xunit;

namespace VvCash.Tests;

/// <summary>Same reproduction shape as ReceiptTemplateStorageTest: a real SQLite
/// file, not a fake, because the bugs this locks in are specific to the real
/// OfflineStorageService — GetSettingAsync runs a bare SELECT with no existence
/// check, and InitializeAsync opens the file with no perimeter around the open
/// itself.</summary>
public class ReceiptTemplateServiceTest : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vvcash-template-svc-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task RefreshAsync_OnAFreshProfileWithNoSettingsTable_DoesNotThrowAndKeepsTheDefault()
    {
        // Reproduces the crash a fresh install/cleared profile hits: App.axaml.cs
        // resolves IReceiptTemplateService and refreshes it before any PosViewModel
        // has ever been constructed, and PosViewModel.InitializeAsync is the only
        // other place that used to call OfflineStorageService.InitializeAsync
        // (which is what creates the Settings table in the first place). No
        // InitializeAsync call here on purpose -- the table must not exist yet.
        var storage = new OfflineStorageService(_dbPath);
        var svc = new ReceiptTemplateService(storage);

        await svc.RefreshAsync();

        AssertIsDefault(svc);
    }

    [Fact]
    public async Task RefreshAsync_OnACorruptDatabaseFile_DoesNotThrowAndKeepsTheDefault()
    {
        // Second live defect from the same review round: a power loss mid-write is
        // the canonical way a cash register's SQLite file gets corrupted, and
        // OfflineStorageService's own comments describe that as a scenario that
        // must leave a working register with an empty catalog, not an unhandled
        // exception. The InitializeAsync call inside RefreshAsync used to sit bare
        // (uncaught) one call frame up in App.axaml.cs; opening a file that is not
        // a valid SQLite database throws "SQLite Error 26: 'file is not a
        // database'" the moment InitializeAsync tries to run its schema check,
        // before MainWindow exists to catch anything.
        File.WriteAllBytes(_dbPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        var storage = new OfflineStorageService(_dbPath);
        var svc = new ReceiptTemplateService(storage);

        await svc.RefreshAsync();

        AssertIsDefault(svc);
    }

    [Fact]
    public async Task RefreshAsync_ExposesTheCachedTemplateAndLogo()
    {
        // Without this, "RefreshAsync always publishes the Default snapshot and
        // never actually reads storage" is a mutation nothing in this file's
        // sibling tests catches -- they only ever exercise the crash paths, where
        // Default is also the correct answer for an unrelated reason.
        var storage = new OfflineStorageService(_dbPath);
        await storage.InitializeAsync();
        await storage.SaveReceiptTemplateAsync("""{"version":1,"width":58,"blocks":[]}""");
        await storage.SaveReceiptLogoAsync("AAECAw==");

        var svc = new ReceiptTemplateService(storage);
        await svc.RefreshAsync();

        Assert.Equal(58, svc.Current.Width);
        Assert.Equal("AAECAw==", svc.Logo);
    }

    [Fact]
    public async Task RefreshAsync_CallsInitializeAsync()
    {
        // Structural half of the fresh-install fix: RefreshAsync itself must be
        // what opens/migrates the database, not rely on something upstream having
        // already done it. Losing this line puts App.axaml.cs right back where
        // Critical #2 found it -- InitializeAsync living in an unguarded spot
        // that runs before ReceiptTemplateService has any chance to catch its
        // failure. The other tests in this file would not necessarily catch that
        // regression on their own: a corrupt or missing-table database throws
        // from GetReceiptTemplateAsync just as readily as from InitializeAsync,
        // so this spy checks the call happened at all, not merely that SOME
        // exception got caught.
        var storage = new InitializeCountingStorage();
        var svc = new ReceiptTemplateService(storage);

        await svc.RefreshAsync();

        Assert.True(storage.InitializeCallCount > 0);
    }

    [Fact]
    public async Task RefreshAsync_ALogoReadFailure_KeepsThePreviouslyLoadedTemplateAndLogo()
    {
        // Reproduced live by review: Current used to be assigned right after the
        // template parsed, with Logo read as a separate following statement --
        // so a failure reading ONLY the logo rolled the catch block back to
        // ReceiptTemplate.Default, discarding a template that had just parsed
        // successfully moments earlier and had nothing to do with the failure.
        // A working register degrading to the default receipt on a transient
        // logo read hiccup is exactly the outcome "любая беда оставляет
        // закэшированное" forbids everywhere else in this codebase.
        var storage = new FlakyLogoStorage
        {
            Template = """{"version":1,"width":48,"blocks":[]}""",
            Logo = "BASE64LOGO",
        };
        var svc = new ReceiptTemplateService(storage);

        await svc.RefreshAsync(); // healthy: both reads succeed
        Assert.Equal(48, svc.Current.Width);
        Assert.Equal("BASE64LOGO", svc.Logo);

        storage.LogoShouldThrow = true;
        await svc.RefreshAsync(); // logo read now fails; template read still succeeds

        Assert.Equal(48, svc.Current.Width);
        Assert.Equal("BASE64LOGO", svc.Logo);
    }

    [Fact]
    public async Task RefreshAsync_NeverPublishesATemplateAndLogoFromTwoDifferentGenerations()
    {
        // CompositePrinterService._printers is volatile precisely because it is
        // written by the background sync loop and read on the print path; the
        // review found ReceiptTemplateService.Current/Logo were plain fields, and
        // -- separately from visibility -- assigned as two independent statements.
        // Two overlapping RefreshAsync calls (the background loop racing "Full
        // reinitialization" from the POS screen) could each finish one of their
        // two assignments before the other call's assignments landed, publishing
        // a template from one generation paired with a logo from another -- a
        // combination that never existed on the server. A single immutable
        // snapshot behind one volatile field, published atomically, plus a
        // semaphore serializing whole RefreshAsync calls, rules this out: the
        // second call's storage reads cannot even start until the first call has
        // fully published its own matched pair and released the gate.
        var storage = new GatedStorage
        {
            Templates = new[]
            {
                """{"version":1,"width":48,"blocks":[]}""",
                """{"version":1,"width":80,"blocks":[]}""",
            },
            Logos = new[] { "LOGO-A", "LOGO-B" },
        };
        var svc = new ReceiptTemplateService(storage);

        // Call #1 (generation A) enters first and blocks inside its own template
        // read, holding the refresh gate for as long as it is blocked.
        var first = svc.RefreshAsync();

        // Call #2 (generation B) must queue behind the gate rather than start
        // reading storage -- with the fix, storage.Templates[1]/Logos[1] are not
        // touched until call #1 finishes and releases the semaphore.
        var second = svc.RefreshAsync();

        storage.ReleaseFirstTemplateRead();

        // Not "await first; assert A; await second; assert B": SemaphoreSlim runs a
        // released waiter's continuation synchronously inside Release() on some
        // runtimes, so by the time "await first" observes completion, "second"
        // may already have run to completion too, nested inside first's own
        // finally block -- there is no reliable observation point between the
        // two. What actually matters, and what the fix promises, is that
        // whichever generation ends up published is a COHERENT pair, never a
        // template from one generation mixed with a logo from the other.
        await Task.WhenAll(first, second);

        var width = svc.Current.Width;
        var logo = svc.Logo;
        var isGenerationA = width == 48 && logo == "LOGO-A";
        var isGenerationB = width == 80 && logo == "LOGO-B";
        Assert.True(isGenerationA || isGenerationB, $"got a torn pair: Width={width} Logo='{logo}'");
    }

    private static void AssertIsDefault(ReceiptTemplateService svc)
    {
        // Structural comparison, not Assert.Same: ReceiptTemplate.Default is a
        // factory (`=> new()`), a fresh instance on every access.
        Assert.Equal(
            JsonSerializer.Serialize(ReceiptTemplate.Default, ReceiptTemplate.Options),
            JsonSerializer.Serialize(svc.Current, ReceiptTemplate.Options));
        Assert.Equal(string.Empty, svc.Logo);
    }

    /// <summary>Every member throws by default -- ReceiptTemplateService only ever
    /// calls the five overridden below, and a fake standing in for the full
    /// interface should refuse anything it wasn't built to expect rather than
    /// silently returning an empty default that could hide a real bug.</summary>
    private class NotSupportedStorage : IOfflineStorageService
    {
        public virtual Task InitializeAsync() => Task.CompletedTask;
        public virtual Task<string> GetReceiptTemplateAsync() => throw new NotSupportedException();
        public virtual Task SaveReceiptTemplateAsync(string raw) => throw new NotSupportedException();
        public virtual Task<string> GetReceiptLogoAsync() => throw new NotSupportedException();
        public virtual Task SaveReceiptLogoAsync(string base64) => throw new NotSupportedException();

        public Task SaveProductsAsync(IEnumerable<Product> products) => throw new NotSupportedException();
        public Task<IEnumerable<Product>> GetAllProductsAsync() => throw new NotSupportedException();
        public Task<IEnumerable<Product>> GetProductsByCategoryAsync(string categoryId) => throw new NotSupportedException();
        public Task<Product?> GetProductByBarcodeAsync(string barcode) => throw new NotSupportedException();
        public Task<IEnumerable<Product>> SearchProductsAsync(string query) => throw new NotSupportedException();
        public Task SaveCategoriesAsync(IEnumerable<Category> categories) => throw new NotSupportedException();
        public Task<IEnumerable<Category>> GetCategoriesAsync() => throw new NotSupportedException();
        public Task SaveQuickAccessCategoriesAsync(IEnumerable<Category> categories) => throw new NotSupportedException();
        public Task<IEnumerable<Category>> GetQuickAccessCategoriesAsync() => throw new NotSupportedException();
        public Task SavePromotionsAsync(IEnumerable<Promotion> promotions) => throw new NotSupportedException();
        public Task<IEnumerable<Promotion>> GetPromotionsAsync() => throw new NotSupportedException();
        public Task ClearPromotionsAsync() => throw new NotSupportedException();
        public Task SaveMoneyPolicyAsync(MoneyPolicy policy) => throw new NotSupportedException();
        public Task<MoneyPolicy> GetMoneyPolicyAsync() => throw new NotSupportedException();
        public Task SaveCashFeaturesAsync(CashFeatures features) => throw new NotSupportedException();
        public Task<CashFeatures> GetCashFeaturesAsync() => throw new NotSupportedException();
        public Task SetLastSyncVersionAsync(int version) => throw new NotSupportedException();
        public Task SaveUnsyncedDocumentAsync(string hash, string payload) => throw new NotSupportedException();
        public Task<IEnumerable<KeyValuePair<string, string>>> GetUnsyncedDocumentsAsync() => throw new NotSupportedException();
        public Task DeleteUnsyncedDocumentAsync(string hash) => throw new NotSupportedException();
        public Task MarkDocumentRejectedAsync(string hash, string reason) => throw new NotSupportedException();
        public Task<int> GetLastSyncVersionAsync() => throw new NotSupportedException();
        public Task ClearCategoriesAsync() => throw new NotSupportedException();
        public Task ClearProductsAsync() => throw new NotSupportedException();
        public Task ApplyRemainsAsync(IReadOnlyDictionary<string, decimal> remains) => throw new NotSupportedException();
        public Task SaveParkedSaleAsync(ParkedSale sale) => throw new NotSupportedException();
        public Task<IEnumerable<ParkedSale>> GetParkedSalesAsync() => throw new NotSupportedException();
        public Task<ParkedSale?> GetParkedSaleAsync(string id) => throw new NotSupportedException();
        public Task DeleteParkedSaleAsync(string id) => throw new NotSupportedException();
        public Task SaveSellersAsync(IEnumerable<SellerInfo> sellers) => throw new NotSupportedException();
        public Task<IEnumerable<SellerInfo>> GetSellersAsync() => throw new NotSupportedException();
    }

    private sealed class InitializeCountingStorage : NotSupportedStorage
    {
        public int InitializeCallCount;

        public override Task InitializeAsync()
        {
            InitializeCallCount++;
            return Task.CompletedTask;
        }

        public override Task<string> GetReceiptTemplateAsync() => Task.FromResult(string.Empty);
        public override Task<string> GetReceiptLogoAsync() => Task.FromResult(string.Empty);
    }

    private sealed class FlakyLogoStorage : NotSupportedStorage
    {
        public string Template = string.Empty;
        public string Logo = string.Empty;
        public bool LogoShouldThrow;

        public override Task<string> GetReceiptTemplateAsync() => Task.FromResult(Template);

        public override Task<string> GetReceiptLogoAsync() => LogoShouldThrow
            ? Task.FromException<string>(new InvalidOperationException("logo read failed"))
            : Task.FromResult(Logo);
    }

    /// <summary>Blocks the FIRST call to enter GetReceiptTemplateAsync until
    /// ReleaseFirstTemplateRead is called, so a test can deterministically start a
    /// second RefreshAsync while the first is still in flight, without any
    /// Task.Delay-based timing.</summary>
    private sealed class GatedStorage : NotSupportedStorage
    {
        private int _templateCallIndex = -1;
        private int _logoCallIndex = -1;
        private readonly TaskCompletionSource _releaseFirstTemplate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string[] Templates = Array.Empty<string>();
        public string[] Logos = Array.Empty<string>();

        public override async Task<string> GetReceiptTemplateAsync()
        {
            var idx = Interlocked.Increment(ref _templateCallIndex);
            if (idx == 0) await _releaseFirstTemplate.Task;
            return Templates[idx];
        }

        public override Task<string> GetReceiptLogoAsync()
        {
            var idx = Interlocked.Increment(ref _logoCallIndex);
            return Task.FromResult(Logos[idx]);
        }

        public void ReleaseFirstTemplateRead() => _releaseFirstTemplate.TrySetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _dbPath + "-journal" })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort cleanup */ }
        }
    }
}
