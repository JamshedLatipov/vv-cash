using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using VvCash.Services.Data;
using VvCash.Services.Queue;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

/// <summary>The backend URL is the one setting that decides where this register's
/// bearer token and cash token are sent. Nothing validated it.</summary>
public class SettingsViewModelTest
{
    /// <summary>Implements IQueueSettings too, like the real SettingsService (see its
    /// own class remarks) — Task 24's mapping tests below need a fake that round-trips
    /// the five queue settings the same way this fake already round-trips everything
    /// else.</summary>
    private sealed class FakeSettings : ISettingsService, IQueueSettings
    {
        public string BackendUrl { get; set; } = string.Empty;
        public string CashRegisterToken { get; set; } = string.Empty;
        public string AuthToken { get; set; } = string.Empty;
        public DateTime? AuthTokenExpiresAt { get; set; }
        public int SyncIntervalMinutes { get; set; } = 10;
        public string Language { get; set; } = "ru";
        public List<PrinterConfig> Printers { get; set; } = new();
        public bool ReturnOpenCashDrawer { get; set; } = true;
        public bool ReturnPrintReceipt { get; set; } = true;
        public string ExchangePayoutCategoryId { get; set; } = string.Empty;
        public string ReturnPayoutCategoryId { get; set; } = string.Empty;
        public string PhoneFormatId { get; set; } = string.Empty;
        public string CustomerDisplayPort { get; set; } = string.Empty;
        public int CustomerDisplayBaudRate { get; set; } = 9600;
        public string CustomerDisplayCodePageId { get; set; } = string.Empty;
        public QueueRole QueueRole { get; set; } = QueueRole.Off;
        public string QueueServerAddress { get; set; } = string.Empty;
        public int QueuePort { get; set; } = 8770;
        public string QueueSecret { get; set; } = string.Empty;
        public int TillIndex { get; set; }
        public int SaveCallCount { get; private set; }
        public event EventHandler? SettingsChanged;
        public void Save()
        {
            SaveCallCount++;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeFeatures : ICashFeatureService
    {
        public CashFeatures Current => CashFeatures.Default;
        public bool HasLoaded => true;
        public Task RefreshAsync() => Task.CompletedTask;
    }

    private sealed class FakePaymentCategories : IPaymentCategoryService
    {
        public Task<List<PaymentCategory>> GetPaymentCategoriesAsync()
            => Task.FromResult(new List<PaymentCategory>());
    }

    private sealed class FakeStorage : IOfflineStorageService
    {
        public Task SaveProductsAsync(IEnumerable<Product> products) => Task.CompletedTask;
        public Task<IEnumerable<Product>> GetAllProductsAsync() => Task.FromResult(Enumerable.Empty<Product>());
        public Task<IEnumerable<Product>> GetProductsByCategoryAsync(string categoryId) => Task.FromResult(Enumerable.Empty<Product>());
        public Task<Product?> GetProductByBarcodeAsync(string barcode) => Task.FromResult<Product?>(null);
        public Task<IEnumerable<Product>> SearchProductsAsync(string query) => Task.FromResult(Enumerable.Empty<Product>());
        public Task SaveCategoriesAsync(IEnumerable<Category> categories) => Task.CompletedTask;
        public Task<IEnumerable<Category>> GetCategoriesAsync() => Task.FromResult(Enumerable.Empty<Category>());
        public Task SaveQuickAccessCategoriesAsync(IEnumerable<Category> categories) => Task.CompletedTask;
        public Task<IEnumerable<Category>> GetQuickAccessCategoriesAsync() => Task.FromResult(Enumerable.Empty<Category>());
        public Task SavePromotionsAsync(IEnumerable<Promotion> promotions) => Task.CompletedTask;
        public Task<IEnumerable<Promotion>> GetPromotionsAsync() => Task.FromResult(Enumerable.Empty<Promotion>());
        public Task ClearPromotionsAsync() => Task.CompletedTask;
        public Task SaveMoneyPolicyAsync(MoneyPolicy policy) => Task.CompletedTask;
        public Task<MoneyPolicy> GetMoneyPolicyAsync() => Task.FromResult(MoneyPolicy.Default);
        public Task SaveCashFeaturesAsync(CashFeatures features) => Task.CompletedTask;
        public Task<CashFeatures> GetCashFeaturesAsync() => Task.FromResult(CashFeatures.Default);
        public Task SaveReceiptTemplateAsync(string raw) => Task.CompletedTask;
        public Task<string> GetReceiptTemplateAsync() => Task.FromResult(string.Empty);
        public Task SaveReceiptLogoAsync(string base64) => Task.CompletedTask;
        public Task<string> GetReceiptLogoAsync() => Task.FromResult(string.Empty);
        public Task SetLastSyncVersionAsync(int version) => Task.CompletedTask;
        public Task SaveUnsyncedDocumentAsync(string hash, string payload) => Task.CompletedTask;
        public Task<IEnumerable<KeyValuePair<string, string>>> GetUnsyncedDocumentsAsync()
            => Task.FromResult(Enumerable.Empty<KeyValuePair<string, string>>());
        public Task DeleteUnsyncedDocumentAsync(string hash) => Task.CompletedTask;
        public Task MarkDocumentRejectedAsync(string hash, string reason) => Task.CompletedTask;
        public Task<int> GetLastSyncVersionAsync() => Task.FromResult(0);
        public int ClearCategoriesCallCount { get; private set; }
        public int ClearProductsCallCount { get; private set; }
        public Task ClearCategoriesAsync() { ClearCategoriesCallCount++; return Task.CompletedTask; }
        public Task ClearProductsAsync() { ClearProductsCallCount++; return Task.CompletedTask; }
        public Task ApplyRemainsAsync(IReadOnlyDictionary<string, decimal> remains) => Task.CompletedTask;
        public Task SaveParkedSaleAsync(ParkedSale sale) => Task.CompletedTask;
        public Task<IEnumerable<ParkedSale>> GetParkedSalesAsync() => Task.FromResult(Enumerable.Empty<ParkedSale>());
        public Task<ParkedSale?> GetParkedSaleAsync(string id) => Task.FromResult<ParkedSale?>(null);
        public Task DeleteParkedSaleAsync(string id) => Task.CompletedTask;
        public Task SaveSellersAsync(IEnumerable<SellerInfo> sellers) => Task.CompletedTask;
        public Task<IEnumerable<SellerInfo>> GetSellersAsync() => Task.FromResult(Enumerable.Empty<SellerInfo>());
        public Task InitializeAsync() => Task.CompletedTask;
    }

    private static SettingsViewModel Build(out FakeSettings settings)
        => Build(out settings, out _);

    private static SettingsViewModel Build(out FakeSettings settings, out FakeStorage storage)
    {
        settings = new FakeSettings();
        storage = new FakeStorage();
        return new SettingsViewModel(
            new MainViewModel(),
            settings,
            storage,
            new FakeFeatures(),
            new FakePaymentCategories());
    }

    /// <summary>Для тестов, которым нужна касса с уже настроенным состоянием:
    /// Build(out …) создаёт FakeSettings сам, и заполнить их до конструктора
    /// вью-модели нечем, а часть настроек читается именно там.</summary>
    private static SettingsViewModel BuildWith(FakeSettings settings)
        => new SettingsViewModel(
            new MainViewModel(),
            settings,
            new FakeStorage(),
            new FakeFeatures(),
            new FakePaymentCategories());

    [Theory]
    [InlineData("http://api.example.test/v1/")]
    [InlineData("example.test")]
    [InlineData("ftp://api.example.test/")]
    [InlineData("not a url at all")]
    public void Save_RefusesAnAddressThatIsNotHttps(string url)
    {
        // Plain http would put the bearer token and the cash token on the wire in the
        // clear, and a bare host or a typo silently produces a register that can never
        // reach anything — the failure only shows up later as "nothing syncs".
        var vm = Build(out var settings);
        vm.BackendUrl = url;

        vm.SaveCommand.Execute(null);

        Assert.Equal(0, settings.SaveCallCount);
        Assert.Equal(string.Empty, settings.BackendUrl);
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage));
    }

    [Fact]
    public void Save_AcceptsAnHttpsAddress()
    {
        var vm = Build(out var settings);
        vm.BackendUrl = "https://api.example.test/v1/";

        vm.SaveCommand.Execute(null);

        Assert.Equal(1, settings.SaveCallCount);
        Assert.Equal("https://api.example.test/v1/", settings.BackendUrl);
        Assert.True(string.IsNullOrEmpty(vm.ErrorMessage));
    }

    [Fact]
    public void Save_AcceptsLocalhostOverHttp()
    {
        // The one address where plain http is not a leak: it never leaves the machine,
        // and it is how the backend is run during development and on-site debugging.
        var vm = Build(out var settings);
        vm.BackendUrl = "http://localhost:8080/api/v1/";

        vm.SaveCommand.Execute(null);

        Assert.Equal(1, settings.SaveCallCount);
        Assert.Equal("http://localhost:8080/api/v1/", settings.BackendUrl);
    }

    // -----------------------------------------------------------------------------
    // The two remaining destructive buttons. This screen opens from the login screen,
    // before anyone has authenticated, and on an offline register a wiped catalog means
    // nothing can be sold until connectivity returns — so neither button may act on a
    // single tap.
    // -----------------------------------------------------------------------------

    [Fact]
    public void ClearProducts_OnlyArmsTheConfirmation_AndTouchesNothing()
    {
        var vm = Build(out _, out var storage);

        vm.ClearProductsCommand.Execute(null);

        Assert.True(vm.IsConfirmVisible);
        Assert.False(string.IsNullOrWhiteSpace(vm.ConfirmMessage));
        Assert.Equal(0, storage.ClearProductsCallCount);
    }

    [Fact]
    public void ClearCategories_OnlyArmsTheConfirmation_AndTouchesNothing()
    {
        var vm = Build(out _, out var storage);

        vm.ClearCategoriesCommand.Execute(null);

        Assert.True(vm.IsConfirmVisible);
        Assert.Equal(0, storage.ClearCategoriesCallCount);
    }

    [Fact]
    public async Task Confirm_RunsTheArmedActionAndClosesTheOverlay()
    {
        var vm = Build(out _, out var storage);
        vm.ClearProductsCommand.Execute(null);

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(1, storage.ClearProductsCallCount);
        Assert.Equal(0, storage.ClearCategoriesCallCount);
        Assert.False(vm.IsConfirmVisible);
    }

    [Fact]
    public async Task CancelConfirm_LeavesStorageAlone_AndDisarmsTheAction()
    {
        // Disarming matters as much as closing: a Confirm arriving later — a stray second
        // tap, a keyboard Enter — must not run an action the operator already refused.
        var vm = Build(out _, out var storage);
        vm.ClearCategoriesCommand.Execute(null);

        vm.CancelConfirmCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(0, storage.ClearCategoriesCallCount);
        Assert.False(vm.IsConfirmVisible);
    }

    [Fact]
    public async Task ArmingASecondActionReplacesTheFirst()
    {
        var vm = Build(out _, out var storage);
        vm.ClearProductsCommand.Execute(null);
        vm.ClearCategoriesCommand.Execute(null);

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(0, storage.ClearProductsCallCount);
        Assert.Equal(1, storage.ClearCategoriesCallCount);
    }

    [Fact]
    public void Save_WritesTheCodePagePerPrinter()
    {
        // На принтер, а не на кассу: в магазине могут стоять две разные железки.
        var vm = Build(out var settings);
        // Save() отказывает без валидного адреса (см. Save_RefusesAnAddressThatIsNotHttps);
        // это не то, что проверяет этот тест, поэтому адрес просто предоставлен.
        vm.BackendUrl = "https://api.example.test/v1/";
        vm.AddPrinterCommand.Execute(null);
        vm.Printers[0].SelectedCodePage = EscPosCodePages.Cp1251;

        vm.SaveCommand.Execute(null);

        Assert.Equal("CP1251", settings.Printers[0].CodePageId);
    }

    [Theory]
    [InlineData("CP1251", false)]
    [InlineData("CP-gone", true)]
    public void Load_ResolvesTheStoredCodePage(string stored, bool expectDefault)
    {
        // Известный id обязателен: на одном лишь неизвестном тест зелёный и без
        // загрузки — инициализатор поля даёт ровно ту же ссылку, что и Resolve.
        var settings = new FakeSettings
        {
            Printers = new List<PrinterConfig>
            {
                new() { Name = "P", ConnectionType = PrinterConnectionType.LAN,
                        ConnectionString = "10.0.0.1:9100", CodePageId = stored }
            }
        };

        var vm = BuildWith(settings);

        Assert.Same(
            expectDefault ? EscPosCodePages.Default : EscPosCodePages.Cp1251,
            vm.Printers[0].SelectedCodePage);
    }

    [Fact]
    public async Task TestPrint_OnAnUnreachablePrinter_ReportsTheReasonRatherThanStayingSilent()
    {
        // Точка проверяет кодовую страницу этой кнопкой, поэтому молчащий отказ
        // означает звонок разработчику — то есть кнопка не сделала того, ради
        // чего заведена.
        //
        // Сокет здесь осознанный: тест именно про то, что причина отказа
        // транспорта доезжает до баннера. Цена — один отказ в соединении, а на
        // этой машине это ~2.2 секунды. Один такой тест терпим; цикла из них
        // здесь быть не должно.
        var vm = Build(out _);
        vm.AddPrinterCommand.Execute(null);
        vm.Printers[0].ConnectionType = PrinterConnectionType.LAN;
        vm.Printers[0].ConnectionString = "127.0.0.1:9199";

        await vm.TestPrintCommand.ExecuteAsync(vm.Printers[0]);

        Assert.True(vm.HasError);
        Assert.Empty(vm.StatusMessage);
        // Не просто "непусто": ErrorMessage — это префикс плюс ex.Message, и тест
        // назван про то, что причина доезжает до баннера. Замени сборку на голый
        // префикс — предыдущие две проверки останутся зелёными, а эта поймает
        // разницу в длине.
        Assert.True(vm.ErrorMessage.Length > I18nService.Instance["TestPrintFailed"].Length);
    }

    [Fact]
    public async Task TestPrint_BuildsFromTheCodePageOnScreen_NotTheSavedOne()
    {
        // Обе кнопки проверки оправданы тем, что строят сервис из несохранённого
        // состояния экрана, а не из настроек — но это ничем не проверялось. Если
        // TestPrint однажды поведут через CompositePrinterService, который читает
        // именно сохранённое, все прочие тесты останутся зелёными, а кнопка
        // перестанет проверять то, ради чего заведена.
        //
        // (PrinterConnectionType)99 — тот же приём, что в
        // CompositePrinterServiceTest.PrintingSurvivesASettingsChangeMidFlight:
        // нарочно вне диапазона enum, уводит EscPosPrinterService.SendAsync в
        // default, который теперь бросает NotSupportedException, не тронув
        // транспорт, — тест по-прежнему не платит ни сетевым таймаутом, ни
        // настоящим портом. TestPrint сама ловит это исключение и пишет в
        // ErrorMessage, но LastTestPrintService присваивается строкой раньше, до
        // await service.PrintTestReceiptAsync(), так что assert ниже до этой ветки
        // не достаёт вовсе.
        //
        // LastTestPrintService — seam ровно как CompositePrinterService.Printers
        // рядом: только для чтения, только для тестов, существует затем, чтобы эту
        // проверку вообще можно было написать.
        var settings = new FakeSettings
        {
            Printers = new List<PrinterConfig>
            {
                new() { Name = "P", ConnectionType = PrinterConnectionType.LAN,
                        ConnectionString = "10.0.0.1:9100", CodePageId = EscPosCodePages.Cp866.Id }
            }
        };
        var vm = BuildWith(settings);

        // Меняем на экране и НЕ сохраняем — Printers[0].SelectedCodePage расходится
        // с тем, что лежит в settings.Printers[0].CodePageId (CP866).
        vm.Printers[0].SelectedCodePage = EscPosCodePages.Cp1251;
        vm.Printers[0].ConnectionType = (PrinterConnectionType)99;

        await vm.TestPrintCommand.ExecuteAsync(vm.Printers[0]);

        Assert.Same(EscPosCodePages.Cp1251, vm.LastTestPrintService?.CodePage);
    }

    [Fact]
    public async Task CheckDisplay_WithNoPortConfigured_DoesNotReportSuccess()
    {
        // Раньше здесь пиналось обратное — «касса без VFD не отказ, значит ОК». Для
        // пути продажи это верно и остаётся верным: NullCustomerDisplayService всё так
        // же возвращает true, чтобы ненастроенная витрина не роняла чек. Но эта кнопка
        // существует ровно затем, чтобы отличить рабочий дисплей от нерабочего, а с
        // пустым портом она строила тот же Null и рапортовала успех — то есть отвечала
        // «работает» про кассу, в порт которой не уходило ни байта.
        //
        // Цена той зелёной галочки — живой разбор: на кассе табло было тёмным, кнопка
        // проверки говорила «ОК», и на этом диагностика останавливалась.
        var vm = BuildWith(new FakeSettings { CustomerDisplayPort = string.Empty });

        await vm.CheckDisplayCommand.ExecuteAsync(null);

        Assert.Empty(vm.StatusMessage);
        Assert.True(vm.HasError);
    }

    [Fact]
    public async Task CheckDisplay_WithNoPortConfigured_SaysThePortIsMissing_NotThatTheDisplayFailed()
    {
        // Два разных исхода, и путать их нельзя: «порт не выбран» чинится в соседней
        // выпадашке, «проверка не удалась» — это уже про железо, кабель и драйвер.
        // Одно сообщение на оба случая отправило бы кассира искать неисправное табло
        // там, где не заполнено поле.
        var vm = BuildWith(new FakeSettings { CustomerDisplayPort = string.Empty });

        await vm.CheckDisplayCommand.ExecuteAsync(null);

        // Сравнение идёт с самим I18nService, а не с готовой строкой, и по-другому здесь
        // нельзя: LoadLanguage читает словарь через AssetLoader.Open("avares://…"), а в
        // тестовом хосте Avalonia не поднята, Initialize никто не зовёт, и словарь
        // остаётся пустым — каждый ключ отдаёт заглушку "[ключ]". То есть проверить
        // здесь можно только «какой ключ выбран», но не «что в нём написано».
        //
        // Что ключ вообще существует во всех пяти словарях, стережёт
        // I18nLocaleTest.DisplayCheckKeys_ExistInEveryLocale — оно читает файлы с диска,
        // мимо AssetLoader, потому и работает.
        Assert.Equal(I18nService.Instance["DisplayCheckNoPort"], vm.ErrorMessage);
        Assert.NotEqual(I18nService.Instance["DisplayCheckFailed"], vm.ErrorMessage);
    }

    // -----------------------------------------------------------------------------
    // Task 24: the five queue settings. Bindings on SettingsView.axaml are reflective
    // (AvaloniaUseCompiledBindingsByDefault is false), so a typo in a binding path
    // compiles clean and only breaks on an actual register — the mapping in and out of
    // IQueueSettings, and the two visibility flags that gate which fields show, get
    // their coverage here instead.
    // -----------------------------------------------------------------------------

    [Fact]
    public void Constructor_LoadsQueueSettingsFromTheService()
    {
        var settings = new FakeSettings
        {
            QueueRole = QueueRole.Client,
            TillIndex = 2,
            QueueServerAddress = "10.0.0.5:8770",
            QueuePort = 9001,
            QueueSecret = "s3cr3t"
        };

        var vm = BuildWith(settings);

        Assert.Equal(QueueRole.Client, vm.QueueRole);
        Assert.Equal("2", vm.TillIndexText);
        Assert.Equal("10.0.0.5:8770", vm.QueueServerAddress);
        Assert.Equal("9001", vm.QueuePortText);
        Assert.Equal("s3cr3t", vm.QueueSecret);
    }

    [Fact]
    public void Save_WritesQueueSettingsBackToTheService()
    {
        var vm = Build(out var settings);
        // Save refuses early on an unacceptable BackendUrl (see the https tests above)
        // — without this, none of the assignments below would ever run.
        vm.BackendUrl = "https://api.example.test/v1/";
        vm.QueueRole = QueueRole.Server;
        vm.TillIndexText = "3";
        vm.QueueServerAddress = "10.0.0.9:8770";
        vm.QueuePortText = "9002";
        vm.QueueSecret = "new-secret";

        vm.SaveCommand.Execute(null);

        Assert.Equal(1, settings.SaveCallCount);
        Assert.Equal(QueueRole.Server, settings.QueueRole);
        Assert.Equal(3, settings.TillIndex);
        Assert.Equal("10.0.0.9:8770", settings.QueueServerAddress);
        Assert.Equal(9002, settings.QueuePort);
        Assert.Equal("new-secret", settings.QueueSecret);
    }

    /// <summary>Unreadable numeric input is skipped, not coerced to a default that
    /// silently overwrites whatever was already configured — the same rule Save already
    /// applies to SyncIntervalText and CustomerDisplayBaudRateText, extended to
    /// QueuePortText and TillIndexText.</summary>
    [Fact]
    public void Save_SkipsUnreadablePortAndTillIndexRatherThanOverwriting()
    {
        var settings = new FakeSettings { QueuePort = 9500, TillIndex = 4 };
        var vm = BuildWith(settings);
        vm.BackendUrl = "https://api.example.test/v1/";
        vm.QueuePortText = "not a number";
        vm.TillIndexText = "not a number either";

        vm.SaveCommand.Execute(null);

        Assert.Equal(1, settings.SaveCallCount);
        Assert.Equal(9500, settings.QueuePort);
        Assert.Equal(4, settings.TillIndex);
    }

    [Theory]
    [InlineData(QueueRole.Off, false, false)]
    [InlineData(QueueRole.Server, true, false)]
    [InlineData(QueueRole.Client, false, true)]
    public void QueueRoleVisibility_GatesServerAndClientFieldsExclusively(
        QueueRole role, bool expectServerFieldsVisible, bool expectClientFieldsVisible)
    {
        var vm = Build(out _);

        vm.QueueRole = role;

        Assert.Equal(expectServerFieldsVisible, vm.IsQueueServerFieldsVisible);
        Assert.Equal(expectClientFieldsVisible, vm.IsQueueClientFieldsVisible);
    }

    // -----------------------------------------------------------------------------
    // Fix 4 (post-review): QueueServer.LastError was read nowhere outside a comment —
    // the spec promises twice that this screen shows it, and it did not. Same reflective-
    // binding caveat as the block above: these cover the view-model side (the string the
    // XAML binds to), not the XAML binding itself.
    // -----------------------------------------------------------------------------

    private static SettingsViewModel BuildWithQueueServerError(FakeSettings settings, string? queueServerError)
        => new SettingsViewModel(
            new MainViewModel(),
            settings,
            new FakeStorage(),
            new FakeFeatures(),
            new FakePaymentCategories(),
            queueServerError: queueServerError,
            localNetworkAddress: () => "10.0.0.7");

    [Fact]
    public void Constructor_SurfacesTheQueueServerErrorWhenTheServerFailedToStart()
    {
        var settings = new FakeSettings { QueueRole = QueueRole.Server, QueueSecret = "s3cr3t" };

        var vm = BuildWithQueueServerError(settings, "Секрет очереди не задан.");

        Assert.Equal("Секрет очереди не задан.", vm.QueueServerError);
        Assert.True(vm.HasQueueServerError);
    }

    [Fact]
    public void Constructor_HasNoQueueServerErrorWhenNoneWasGiven()
    {
        var settings = new FakeSettings { QueueRole = QueueRole.Server, QueueSecret = "s3cr3t" };

        var vm = BuildWithQueueServerError(settings, queueServerError: null);

        Assert.Equal(string.Empty, vm.QueueServerError);
        Assert.False(vm.HasQueueServerError);
    }

    /// <summary>The 401 body a rejected /board or /kds request carries points the reader
    /// at "the queue settings on the server register" for a link — this is that link.
    /// Built from what is actually saved (QueuePort/QueueSecret), matching what the
    /// already-running Kestrel instance actually accepts — see Fix 5 for why that is not
    /// the same as whatever is currently typed in QueuePortText/QueueSecret.</summary>
    [Fact]
    public void Constructor_BuildsBoardAndKdsLinksForAConfiguredServer()
    {
        var settings = new FakeSettings { QueueRole = QueueRole.Server, QueuePort = 9001, QueueSecret = "s3cr3t" };

        var vm = BuildWithQueueServerError(settings, queueServerError: null);

        Assert.Equal("http://10.0.0.7:9001/board?secret=s3cr3t", vm.BoardUrl);
        Assert.Equal("http://10.0.0.7:9001/kds?secret=s3cr3t", vm.KdsUrl);
        Assert.True(vm.HasQueueScreenLinks);
    }

    [Fact]
    public void Constructor_EscapesASecretThatNeedsItInTheScreenLinks()
    {
        var settings = new FakeSettings { QueueRole = QueueRole.Server, QueuePort = 9001, QueueSecret = "a b&c" };

        var vm = BuildWithQueueServerError(settings, queueServerError: null);

        Assert.Equal("http://10.0.0.7:9001/board?secret=a%20b%26c", vm.BoardUrl);
    }

    [Fact]
    public void Constructor_HasNoScreenLinksWhenThisTillIsNotTheServer()
    {
        var settings = new FakeSettings { QueueRole = QueueRole.Client, QueuePort = 9001, QueueSecret = "s3cr3t" };

        var vm = BuildWithQueueServerError(settings, queueServerError: null);

        Assert.Equal(string.Empty, vm.BoardUrl);
        Assert.Equal(string.Empty, vm.KdsUrl);
        Assert.False(vm.HasQueueScreenLinks);
    }

    /// <summary>An empty secret is the state QueueServer.StartAsync itself refuses to
    /// open a port for (see its own remarks) — a link built anyway would point at a
    /// server that was never listening.</summary>
    [Fact]
    public void Constructor_HasNoScreenLinksWhenTheSecretIsEmpty()
    {
        var settings = new FakeSettings { QueueRole = QueueRole.Server, QueuePort = 9001, QueueSecret = "" };

        var vm = BuildWithQueueServerError(settings, queueServerError: null);

        Assert.Equal(string.Empty, vm.BoardUrl);
        Assert.False(vm.HasQueueScreenLinks);
    }
}
