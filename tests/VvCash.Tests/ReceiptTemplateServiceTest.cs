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
    public async Task RefreshAsync_NeverExposesATornPairToAReaderMidUpdate()
    {
        // Round 3 of review: the previous version of this test (see git history)
        // only asserted state AFTER both overlapping RefreshAsync calls had fully
        // completed -- by then the semaphore had already serialized them, so a
        // mutation that publishes the snapshot in two separate steps (first
        // Current with the new template and the STALE old logo, then a second
        // assignment with the new logo) still left every one of 1148 tests green:
        // both steps finish well before the test's next check.
        //
        // That mutation matters because the semaphore protects WRITERS from each
        // other; it says nothing about READERS. The print path -- the only real
        // consumer of Current/Logo -- never touches _refreshGate at all. So the
        // property worth pinning is not "the writers don't interleave" (already
        // covered elsewhere) but "a reader can NEVER observe a half-published
        // pair, at any point during an update" -- which is exactly what a single
        // volatile Snapshot assigned once is for.
        //
        // This test IS that reader: it inspects Current/Logo from outside
        // RefreshAsync entirely, at a moment deliberately frozen mid-update by
        // gating the SECOND generation's logo read (its template read completes
        // immediately). A correct implementation reads both raw values into
        // locals and only then publishes one Snapshot, so nothing has changed
        // yet -- the reader still sees generation A's matched pair in full. The
        // two-step mutation described above would have already overwritten
        // _snapshot with (generation B template, generation A logo) by this
        // point -- a pair that never existed on the server.
        var storage = new LogoGatedStorage(blockAtLogoCallIndex: 1)
        {
            Templates = new[]
            {
                """{"version":1,"width":48,"blocks":[]}""",
                """{"version":1,"width":80,"blocks":[]}""",
            },
            Logos = new[] { "LOGO-A", "LOGO-B" },
        };
        var svc = new ReceiptTemplateService(storage);

        // Generation A settles fully and synchronously -- its own logo read
        // (call index 0) is not gated.
        await svc.RefreshAsync();
        Assert.Equal(48, svc.Current.Width);
        Assert.Equal("LOGO-A", svc.Logo);

        // Generation B's template read (idx 1) returns immediately; its logo
        // read (also idx 1, the second-ever call) blocks. Everything up to and
        // including the template read has already run by the time control
        // returns here -- a two-step publisher would have written the new
        // template already.
        var second = svc.RefreshAsync();

        Assert.Equal(48, svc.Current.Width);
        Assert.Equal("LOGO-A", svc.Logo);

        storage.ReleaseLogoRead();
        await second;

        Assert.Equal(80, svc.Current.Width);
        Assert.Equal("LOGO-B", svc.Logo);
    }

    [Fact]
    public async Task CurrentTemplateAndLogo_UnderConcurrentRefreshes_NeverTearsAcrossGenerations()
    {
        // Заменяет предыдущую версию этого теста (см. историю файла), которая
        // называла в комментарии ровно ту регрессию, что и эта, но не могла
        // её поймать: подделка хранилища там замораживала RefreshAsync ДО
        // публикации нового снимка, так что единственное, что читал внешний
        // вызывающий за время заморозки, — старый, ещё не тронутый снимок.
        // Реализация CurrentTemplateAndLogo как `(Current, Logo)` (то самое
        // двойное чтение, которое этот метод обязан не делать) прошла бы ту
        // версию теста с тем же результатом, что и правильная: ни один из
        // двух реальных вызовов _snapshot внутри такой реализации не попадал
        // в окно между записями, потому что запись в тот момент вообще не
        // происходила. Ревью подтвердило это отдельным стресс-прогоном:
        // двойное чтение — 4.5 миллиона рваных пар за секунды, у
        // CurrentTemplateAndLogo — ноль.
        //
        // Здесь — настоящая гонка, а не заморозка: один поток непрерывно
        // публикует новые поколения (RefreshAsync без единой паузы — подделка
        // хранилища ниже ничего не ждёт), второй непрерывно читает
        // CurrentTemplateAndLogo и сверяет текст единственного TextBlock в
        // шаблоне с Logo. Подделка кодирует номер поколения в оба значения
        // одинаково (Content блока = "N", Logo = "N"), так что рассинхрон
        // ловится как несовпадение строк, а не как исключение или зависание.
        //
        // Текст блока, а не ReceiptTemplate.Width: первая версия этой
        // подделки кодировала номер поколения в Width, и тест ложно краснел —
        // ReceiptTemplate.Width клампится в сеттере потолком в 200
        // (MaxWidth), так что после 200-го поколения Width замирал на 200,
        // пока Logo продолжал расти дальше, и КАЖДОЕ следующее чтение
        // показывало "рассинхрон", хотя _snapshot был опубликован полностью
        // согласованным (число из diagnostic-лога T:/L: совпадало на каждом
        // поколении). Причина была не в гонке, а в том, что ширина ленты —
        // клампящееся поле, непригодное для переноски произвольно большого
        // счётчика поколений; TextBlock.Content такого потолка не имеет.
        //
        // Ложноположительный результат здесь невозможен по конструкции: если
        // CurrentTemplateAndLogo читает _snapshot РОВНО ОДИН раз в локальную
        // переменную и берёт оба значения из неё, то сколько бы записывающий
        // поток ни переставлял ссылку в _snapshot за это время, оба значения
        // всегда приходят из ОДНОГО и того же уже прочитанного (неизменяемого)
        // объекта Snapshot.
        var storage = new IncrementingGenerationStorage();
        var svc = new ReceiptTemplateService(storage);
        await svc.RefreshAsync(); // поколение 1 публикуется до старта гонки

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        string? tornPair = null;

        var writer = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
                await svc.RefreshAsync();
        });

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested && tornPair == null)
            {
                var (template, logo) = svc.CurrentTemplateAndLogo;
                var content = ((TextBlock)template.Blocks[0]).Content;
                if (content != logo)
                    tornPair = $"blockContent={content}, logo={logo}";
            }
        });

        await Task.WhenAll(writer, reader);

        Assert.Null(tornPair);
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

    /// <summary>Template reads never block. The logo read whose zero-based call
    /// index equals <paramref name="blockAtLogoCallIndex"/> blocks until
    /// ReleaseLogoRead is called -- letting a test freeze RefreshAsync AFTER it
    /// has read (and, under a buggy two-step publisher, already applied) the new
    /// template, but BEFORE its matching new logo lands, so a reader outside the
    /// refresh gate can be asked what it sees at that exact moment.</summary>
    private sealed class LogoGatedStorage : NotSupportedStorage
    {
        private readonly int _blockAtLogoCallIndex;
        private int _templateCallIndex = -1;
        private int _logoCallIndex = -1;
        private readonly TaskCompletionSource _releaseLogoRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LogoGatedStorage(int blockAtLogoCallIndex) => _blockAtLogoCallIndex = blockAtLogoCallIndex;

        public string[] Templates = Array.Empty<string>();
        public string[] Logos = Array.Empty<string>();

        public override Task<string> GetReceiptTemplateAsync()
        {
            var idx = Interlocked.Increment(ref _templateCallIndex);
            return Task.FromResult(Templates[idx]);
        }

        public override async Task<string> GetReceiptLogoAsync()
        {
            var idx = Interlocked.Increment(ref _logoCallIndex);
            if (idx == _blockAtLogoCallIndex) await _releaseLogoRead.Task;
            return Logos[idx];
        }

        public void ReleaseLogoRead() => _releaseLogoRead.TrySetResult();
    }

    /// <summary>Non-blocking storage for
    /// CurrentTemplateAndLogo_UnderConcurrentRefreshes_NeverTearsAcrossGenerations:
    /// every read resolves immediately (Task.FromResult, no await gap), so a
    /// writer loop can publish as many generations per second as the CPU
    /// allows -- the stress test needs volume, not a controlled pause point.
    ///
    /// One counter, incremented only by GetReceiptTemplateAsync. Within a
    /// single RefreshAsync call, GetReceiptTemplateAsync runs first (bumping
    /// _generation to N) and GetReceiptLogoAsync runs second, reading
    /// _generation back as N -- so the template's block text and Logo always
    /// encode the SAME generation number for a given RefreshAsync, as long as
    /// calls stay sequential (this test drives exactly one writer, never two
    /// concurrent RefreshAsync). A reader observing a mismatch has caught a
    /// torn snapshot.
    ///
    /// The generation number goes into a TextBlock's Content, not into
    /// Width: Width is clamped in its setter to ReceiptTemplate.MaxWidth
    /// (200), so an unbounded counter written there freezes at 200 forever
    /// once the stress loop runs past generation 200 -- while Logo keeps
    /// climbing unclamped. That produced a "torn pair" on every single read
    /// after generation 200 with a CORRECT CurrentTemplateAndLogo (confirmed
    /// with a diagnostic log: every published Snapshot paired the same
    /// generation number, single-threaded, no interleaving) -- a test-fixture
    /// bug, not the regression this test exists to catch. TextBlock.Content
    /// has no such ceiling.</summary>
    private sealed class IncrementingGenerationStorage : NotSupportedStorage
    {
        private int _generation;

        public override Task<string> GetReceiptTemplateAsync()
        {
            var gen = Interlocked.Increment(ref _generation);
            return Task.FromResult(
                $$"""{"version":1,"width":32,"blocks":[{"type":"text","content":"{{gen}}"}]}""");
        }

        public override Task<string> GetReceiptLogoAsync() => Task.FromResult(_generation.ToString());
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
