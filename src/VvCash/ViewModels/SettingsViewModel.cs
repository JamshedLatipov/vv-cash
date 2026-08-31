using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Constants;
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using VvCash.Services.Data;
using VvCash.Services.Hardware;
using VvCash.Services.Queue;

namespace VvCash.ViewModels;

public partial class PrinterConfigViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _connectionString = string.Empty;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLan))]
    [NotifyPropertyChangedFor(nameof(IsUsbOrCom))]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel))]
    private PrinterConnectionType _connectionType;

    /// <summary>Каталог целиком: он неизменен и не зависит от сети — как
    /// AvailablePhoneFormats рядом.</summary>
    public IReadOnlyList<EscPosCodePage> AvailableCodePages { get; } = EscPosCodePages.All;

    /// <summary>Nullable по той же причине, что SelectedPhoneFormat: SelectingItemsControl
    /// приводит SelectedItem к null и пишет его обратно через TwoWay, если присвоенного
    /// значения не нашлось в ItemsSource.</summary>
    [ObservableProperty]
    private EscPosCodePage? _selectedCodePage = EscPosCodePages.Default;

    /// <summary>Роли держатся набором флагов, а на экране — тремя независимыми
    /// галками: «печатает чеки и бегунки» это обычная настройка, а не исключение.
    /// Хранить их тремя bool и собирать флаги на сохранении было бы вторым
    /// источником правды — здесь один, а галки его проекции.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrintsReceipt))]
    [NotifyPropertyChangedFor(nameof(PrintsTicket))]
    [NotifyPropertyChangedFor(nameof(PrintsKitchenOrder))]
    private PrintRole _roles = PrintRole.Receipt;

    public bool PrintsReceipt
    {
        get => Roles.HasFlag(PrintRole.Receipt);
        set => Roles = value ? Roles | PrintRole.Receipt : Roles & ~PrintRole.Receipt;
    }

    public bool PrintsTicket
    {
        get => Roles.HasFlag(PrintRole.Ticket);
        set => Roles = value ? Roles | PrintRole.Ticket : Roles & ~PrintRole.Ticket;
    }

    public bool PrintsKitchenOrder
    {
        get => Roles.HasFlag(PrintRole.KitchenOrder);
        set => Roles = value ? Roles | PrintRole.KitchenOrder : Roles & ~PrintRole.KitchenOrder;
    }

    public ObservableCollection<string> AvailableConnections { get; } = new();

    public bool IsLan => ConnectionType == PrinterConnectionType.LAN;
    public bool IsUsbOrCom => ConnectionType == PrinterConnectionType.USB || ConnectionType == PrinterConnectionType.COM;

    public string ConnectionLabel => ConnectionType switch
    {
        PrinterConnectionType.LAN => "IP Address / Port",
        PrinterConnectionType.USB => "Select Printer",
        PrinterConnectionType.COM => "Select Port",
        _ => "Address"
    };

    partial void OnConnectionTypeChanged(PrinterConnectionType value)
    {
        UpdateAvailableConnections();
    }

    public void UpdateAvailableConnections()
    {
        AvailableConnections.Clear();
        if (ConnectionType == PrinterConnectionType.USB)
        {
            var printers = PrinterDiscoveryService.GetUsbPrinters();
            foreach (var printer in printers)
                AvailableConnections.Add(printer);
        }
        else if (ConnectionType == PrinterConnectionType.COM)
        {
            var ports = PrinterDiscoveryService.GetComPorts();
            foreach (var port in ports)
                AvailableConnections.Add(port);
        }

        if (IsUsbOrCom && !AvailableConnections.Contains(ConnectionString) && AvailableConnections.Any())
        {
            ConnectionString = AvailableConnections.First();
        }
        else if (IsLan && string.IsNullOrWhiteSpace(ConnectionString))
        {
            ConnectionString = "192.168.1.100:9100";
        }
    }
}

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly IOfflineStorageService _offlineStorageService;
    private readonly ICashFeatureService _features;
    private readonly IPaymentCategoryService? _paymentCategories;

    [ObservableProperty]
    private string _backendUrl = string.Empty;

    /// <summary>Why the last Save was refused, or empty when it went through. Shown on
    /// the settings screen itself: a Save that silently does nothing is worse than one
    /// that refuses out loud, because the register then runs on whatever was configured
    /// before while the screen shows what the operator thinks they just set.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>Результат кнопок проверки, когда он не отказ. Отдельно от
    /// ErrorMessage: тот красный, с иконкой предупреждения, и «Пробный чек
    /// отправлен» в такой рамке читается как отказ. Пустеет при каждой новой
    /// проверке, чтобы прошлый успех не висел над свежей ошибкой.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = string.Empty;

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    [ObservableProperty]
    private string _cashRegisterToken = string.Empty;

    [ObservableProperty]
    private string _syncIntervalText = string.Empty;

    public ObservableCollection<PrinterConfigViewModel> Printers { get; } = new();

    public ObservableCollection<string> AvailableLanguages { get; } = new() { "ru", "en", "tg", "uz", "kk" };

    [ObservableProperty]
    private string _selectedLanguage = "ru";

    /// <summary>Каталог целиком: он неизменен и не зависит от сети, поэтому
    /// подгружать его нечем и незачем.</summary>
    public IReadOnlyList<PhoneFormat> AvailablePhoneFormats { get; } = PhoneFormats.All;

    /// <summary>Nullable, как и SelectedPaymentCategory рядом: SelectingItemsControl
    /// приводит SelectedItem к null и пишет его обратно через TwoWay-привязку, если
    /// присвоенного значения не нашлось в ItemsSource. Сегодня оно находится всегда —
    /// но лишь потому, что Resolve возвращает экземпляры из All, а PhoneFormat не
    /// переопределяет Equals; на этот незаписанный инвариант опираться не стоит.</summary>
    [ObservableProperty]
    private PhoneFormat? _selectedPhoneFormat = PhoneFormats.Default;

    [ObservableProperty]
    private bool _returnOpenCashDrawer = true;

    [ObservableProperty]
    private bool _returnPrintReceipt = true;

    [ObservableProperty]
    private string _customerDisplayPort = string.Empty;

    /// <summary>Строкой, а не int: то же, что SyncIntervalText рядом — TextBox с
    /// частично набранным числом не должен ронять привязку.</summary>
    [ObservableProperty]
    private string _customerDisplayBaudRateText = "9600";

    [ObservableProperty]
    private EscPosCodePage? _selectedDisplayCodePage = EscPosCodePages.Default;

    /// <summary>COM-порты машины. Тот же источник, что у принтеров на COM.</summary>
    public ObservableCollection<string> AvailableDisplayPorts { get; } = new();

    public IReadOnlyList<EscPosCodePage> AvailableCodePages { get; } = EscPosCodePages.All;

    /// <summary>Task 24: пять настроек очереди заказов и кухни (см.
    /// IQueueSettings). Читаются/пишутся через приведение _settingsService к
    /// IQueueSettings, которую та же SettingsService реализует наравне с
    /// ISettingsService — тем же приёмом, что App.axaml.cs использует для
    /// NumberPool/QueueServer, а не через второй впрыснутый сервис.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQueueServerFieldsVisible))]
    [NotifyPropertyChangedFor(nameof(IsQueueClientFieldsVisible))]
    private QueueRole _queueRole = QueueRole.Off;

    /// <summary>Значения роли для ComboBox — как ConnectionTypes у принтера
    /// чуть выше: без DisplayMemberBinding, так что на экране это те же
    /// "Off"/"Server"/"Client", что и в самом enum (тот же нелокализованный
    /// приём, что и у ConnectionTypes).</summary>
    public Array QueueRoles => Enum.GetValues(typeof(QueueRole));

    /// <summary>Порт и секрет сервера очереди видны только тогда, когда эта
    /// касса сама назначена сервером: клиенту нечего слушать, и показывать
    /// эти поля значило бы предлагать настроить порт, который эта касса
    /// никогда не откроет (см. QueueServer.StartAsync).</summary>
    public bool IsQueueServerFieldsVisible => QueueRole == QueueRole.Server;

    /// <summary>Адрес кассы-сервера имеет смысл только у клиента — сервер
    /// сам себя по этому адресу не набирает (см. App.axaml.cs: сервер ходит
    /// к себе по 127.0.0.1, минуя QueueServerAddress вовсе).</summary>
    public bool IsQueueClientFieldsVisible => QueueRole == QueueRole.Client;

    [ObservableProperty]
    private string _queueServerAddress = string.Empty;

    /// <summary>Строкой, а не int — тот же приём, что SyncIntervalText и
    /// CustomerDisplayBaudRateText выше: TextBox с частично набранным числом
    /// не должен ронять привязку.</summary>
    [ObservableProperty]
    private string _queuePortText = SettingsData.DefaultQueuePort.ToString();

    [ObservableProperty]
    private string _queueSecret = string.Empty;

    /// <summary>Номер этой кассы в пуле номеров очереди (см.
    /// IQueueSettings.TillIndex) — строкой по той же причине, что и
    /// QueuePortText выше. Виден на экране ВСЕГДА, а не только у клиента:
    /// сервер тоже продаёт и тоже выдаёт номера из своего диапазона (класс
    /// вычетов Number % NumberPool.Tills). Две кассы с одинаковым номером
    /// начнут выдавать покупателям одинаковые номера — по этой же причине
    /// IQueueSettings.TillIndex зажимает его в 0..NumberPool.Tills-1, а не
    /// принимает как есть.</summary>
    [ObservableProperty]
    private string _tillIndexText = "0";

    /// <summary>Fix 4: причина, по которой сервер очереди этой кассы не поднялся —
    /// занятый порт, пустой секрет. QueueServer.LastError уже нёс её, но экран
    /// настроек нигде её не читал, хотя спека дважды обещает, что он её покажет —
    /// касса в этом состоянии видела бы, что /kds и /board просто не грузятся, без
    /// единого слова о причине. Приходит готовой строкой через конструктор, а не
    /// вычисляется здесь: единственный живой QueueServer создаётся один раз в
    /// App.axaml.cs (см. его remarks про время жизни поля) и не виден отсюда
    /// никаким другим путём, а плодить второй впрыснутый сервис ради одного
    /// строкового поля — то, чего Task 24 уже избежал приведением к
    /// IQueueSettings чуть выше. Null, когда эта касса не Server вовсе (сервер и
    /// не пытался стартовать) или когда последняя попытка была успешной.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQueueServerError))]
    private string _queueServerError = string.Empty;

    public bool HasQueueServerError => !string.IsNullOrWhiteSpace(QueueServerError);

    /// <summary>Fix 4, вторая половина: 401-ответ сервера очереди отсылает за
    /// ссылкой "в настройки очереди на кассе-сервере" (см. QueueServer —
    /// сообщение при отсутствующем секрете), а самого поля со ссылкой там не
    /// было. Строится один раз в конструкторе из того, что реально сохранено в
    /// настройках (QueuePort/QueueSecret), а не из QueuePortText/QueueSecret —
    /// тех самых полей на экране, которые можно тут же начать редактировать: Fix 5
    /// объясняет, почему это разные вещи — запущенный сейчас Kestrel слушает
    /// СТАРЫЙ порт и требует СТАРЫЙ секрет до перезапуска, и ссылка обязана вести
    /// туда, куда реально можно достучаться прямо сейчас, а не туда, что
    /// оператор только что напечатал и ещё не сохранил. Пусто, если роль этой
    /// кассы не Server (тогда сервер здесь не поднимается вовсе — нечего
    /// показывать) или секрет пуст (StartAsync в этом случае и не открывает
    /// порт — см. его remarks, ссылка была бы ссылкой в никуда).</summary>
    public string BoardUrl { get; private set; } = string.Empty;

    public string KdsUrl { get; private set; } = string.Empty;

    public bool HasQueueScreenLinks => !string.IsNullOrEmpty(BoardUrl);

    /// <summary>Payment categories offered for the exchange payout, loaded from
    /// GET /documents/payment/categories/. Empty when the register is offline or the
    /// role lacks documents.PaymentCategoryList — in which case the previously saved id
    /// is left alone rather than being cleared by an empty dropdown (see Save).</summary>
    public ObservableCollection<PaymentCategory> PaymentCategories { get; } = new();

    [ObservableProperty]
    private PaymentCategory? _selectedPaymentCategory;

    /// <summary>The same list, picked separately for the returns screen's payout —
    /// see ISettingsService.ReturnPayoutCategoryId for why the two are not one
    /// setting. Both dropdowns read <see cref="PaymentCategories"/>; a ComboBox does
    /// not mutate its ItemsSource, so one collection serves both.</summary>
    [ObservableProperty]
    private PaymentCategory? _selectedReturnPaymentCategory;

    /// <summary>What the register will actually do, which is the server's answer.
    /// The local fields behind the old checkboxes are kept and still saved, but no
    /// longer consulted — see ReturnsViewModel.
    ///
    /// Can read stale (show "enabled" even when the server has switched a flag off)
    /// on one specific path: this screen is reachable from the login screen, before
    /// any shift has been opened this run of the process, and ICashFeatureService's
    /// cache is only populated by PosViewModel.InitializeAsync — refreshing it any
    /// earlier is impossible, not just undone, because that same InitializeAsync is
    /// what creates the local database's Settings table in the first place
    /// (OfflineStorageService.InitializeAsync); reading the cache before that table
    /// exists would fail outright. Reordering startup to fix this display-only gap
    /// is deliberately out of scope: that sequencing already caused one production
    /// lockout (see the expired-session-escape fix), so it stays as-is for a toggle
    /// that is read-only here anyway.
    ///
    /// This never affects what actually happens on a return: RunPostReturnActionsAsync
    /// runs after a shift has opened, by which point InitializeAsync has already
    /// refreshed the real flags. Only what this screen *shows*, before that point,
    /// can lag behind — never what the register *does*.</summary>
    public bool ReturnOpenCashDrawerEffective => _features.Current.IsEnabled(CashFeatureCodes.ReturnOpenDrawer);
    public bool ReturnPrintReceiptEffective => _features.Current.IsEnabled(CashFeatureCodes.ReturnPrintReceipt);

    public Array ConnectionTypes => Enum.GetValues(typeof(PrinterConnectionType));

    public Action<ViewModelBase>? NavigationRequest { get; set; }
    private ViewModelBase _previousViewModel;

    public SettingsViewModel(ViewModelBase previousViewModel, ISettingsService settingsService,
        IOfflineStorageService offlineStorageService, ICashFeatureService features,
        IPaymentCategoryService? paymentCategories = null,
        // Fix 4: см. remarks на QueueServerError выше для того, откуда это
        // берётся и почему параметром, а не вторым сервисом.
        string? queueServerError = null,
        // Тестовый шов для BoardUrl/KdsUrl ниже — production-код (App.axaml.cs) не
        // передаёт его и получает DefaultLocalNetworkAddress без изменений; тесты
        // подставляют фиксированный хост, чтобы не зависеть от того, какие сетевые
        // интерфейсы подняты на машине, где они выполняются.
        Func<string>? localNetworkAddress = null)
    {
        _previousViewModel = previousViewModel;
        _settingsService = settingsService;
        _offlineStorageService = offlineStorageService;
        _features = features;
        _paymentCategories = paymentCategories;

        // Load existing settings
        BackendUrl = _settingsService.BackendUrl;
        CashRegisterToken = _settingsService.CashRegisterToken;
        SyncIntervalText = _settingsService.SyncIntervalMinutes.ToString();
        SelectedLanguage = string.IsNullOrEmpty(_settingsService.Language) ? "ru" : _settingsService.Language;
        SelectedPhoneFormat = PhoneFormats.Resolve(_settingsService.PhoneFormatId);
        ReturnOpenCashDrawer = _settingsService.ReturnOpenCashDrawer;
        ReturnPrintReceipt = _settingsService.ReturnPrintReceipt;

        CustomerDisplayPort = _settingsService.CustomerDisplayPort;
        CustomerDisplayBaudRateText = _settingsService.CustomerDisplayBaudRate.ToString();
        SelectedDisplayCodePage = EscPosCodePages.Resolve(_settingsService.CustomerDisplayCodePageId);
        foreach (var port in PrinterDiscoveryService.GetComPorts())
            AvailableDisplayPorts.Add(port);
        // Сохранённый порт мог быть не переподключён к моменту открытия экрана — тот
        // же случай, что и у ConnectionString принтера на COM (см.
        // PrinterConfigViewModel.UpdateAvailableConnections). Не добавить его сюда —
        // значит отдать CustomerDisplayPort на слом первой же простановке
        // SelectedItem: SelectingItemsControl пишет null назад для значения, которого
        // нет в ItemsSource (см. комментарий у SelectedCodePage выше), а Save ниже
        // сохранил бы этот null поверх настроенного порта.
        if (!string.IsNullOrWhiteSpace(CustomerDisplayPort) && !AvailableDisplayPorts.Contains(CustomerDisplayPort))
            AvailableDisplayPorts.Add(CustomerDisplayPort);

        // Task 24: cast rather than a second injected service — see this block's own
        // class-level remarks just above the properties it fills in.
        if (_settingsService is IQueueSettings queueSettings)
        {
            QueueRole = queueSettings.QueueRole;
            QueueServerAddress = queueSettings.QueueServerAddress;
            QueuePortText = queueSettings.QueuePort.ToString();
            QueueSecret = queueSettings.QueueSecret;
            TillIndexText = queueSettings.TillIndex.ToString();

            QueueServerError = queueServerError ?? string.Empty;

            // Только для Server и только с непустым секретом — те же два условия,
            // под которыми QueueServer.StartAsync вообще открывает порт (см. его
            // remarks). Из QueuePort/QueueSecret настроек, а не из QueuePortText/
            // QueueSecret на экране — см. remarks на BoardUrl выше.
            if (queueSettings.QueueRole == QueueRole.Server && !string.IsNullOrWhiteSpace(queueSettings.QueueSecret))
            {
                var host = (localNetworkAddress ?? DefaultLocalNetworkAddress)();
                var secret = Uri.EscapeDataString(queueSettings.QueueSecret);
                BoardUrl = $"http://{host}:{queueSettings.QueuePort}/board?secret={secret}";
                KdsUrl = $"http://{host}:{queueSettings.QueuePort}/kds?secret={secret}";
            }
        }

        foreach (var printer in _settingsService.Printers)
        {
            var vm = new PrinterConfigViewModel
            {
                Name = printer.Name,
                ConnectionType = printer.ConnectionType,
                ConnectionString = printer.ConnectionString,
                IsEnabled = printer.IsEnabled,
                SelectedCodePage = EscPosCodePages.Resolve(printer.CodePageId),
                Roles = printer.Roles
            };
            vm.UpdateAvailableConnections();
            Printers.Add(vm);
        }

        _ = LoadPaymentCategoriesAsync();
    }

    /// <summary>Fix 4: наилучший вариант LAN-адреса этой машины для ссылок
    /// BoardUrl/KdsUrl — телевизору или планшету другого устройства нечем
    /// достучаться до localhost/127.0.0.1 этой кассы, ему нужен адрес в
    /// локальной сети точки. Первый не loopback, рабочий (Up) IPv4-адрес —
    /// тот же практический выбор, которым точку обычно и описывают в
    /// инструкции (одна активная сеть на месте продаж); адреса
    /// автоконфигурации (169.254.x.x — сеть не поднялась вовсе) пропускаются,
    /// поскольку такой адрес недостижим ни для кого другого на точке.
    /// "localhost", если подходящего адреса не нашлось или перечисление
    /// интерфейсов само упало (редко, но бывает на части виртуалок) — хуже,
    /// чем настоящий LAN-адрес, но лучше, чем пустая ссылка или упавший экран
    /// настроек.</summary>
    internal static string DefaultLocalNetworkAddress()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    if (System.Net.IPAddress.IsLoopback(addr.Address)) continue;

                    var bytes = addr.Address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue; // APIPA

                    return addr.Address.ToString();
                }
            }
        }
        catch
        {
            // Сбой перечисления интерфейсов не должен ронять экран настроек ради
            // одной удобной ссылки — см. remarks выше.
        }
        return "localhost";
    }

    private async Task LoadPaymentCategoriesAsync()
    {
        if (_paymentCategories == null) return;
        var categories = await _paymentCategories.GetPaymentCategoriesAsync();
        PaymentCategories.Clear();
        foreach (var c in categories) PaymentCategories.Add(c);
        SelectedPaymentCategory = PaymentCategories
            .FirstOrDefault(c => c.Id == _settingsService.ExchangePayoutCategoryId);
        SelectedReturnPaymentCategory = PaymentCategories
            .FirstOrDefault(c => c.Id == _settingsService.ReturnPayoutCategoryId);
    }

    [RelayCommand]
    private void AddPrinter()
    {
        var vm = new PrinterConfigViewModel
        {
            Name = "New Printer",
            ConnectionType = PrinterConnectionType.LAN,
            ConnectionString = "192.168.1.100:9100",
            IsEnabled = true,
            SelectedCodePage = EscPosCodePages.Default
        };
        vm.UpdateAvailableConnections();
        Printers.Add(vm);
    }

    [RelayCommand]
    private void RemovePrinter(PrinterConfigViewModel printer)
    {
        if (printer != null)
        {
            Printers.Remove(printer);
        }
    }

    /// <summary>Что в последний раз построил TestPrint — только для чтения и только
    /// для тестов. То же обоснование, что у CompositePrinterService.Printers: строка,
    /// которая доносит кодовую страницу СО ЭКРАНА (а не из настроек) до сервиса
    /// печати, иначе не покрывается ничем, и её подмену на CompositePrinterService,
    /// читающий сохранённое, ловил бы только grep.</summary>
    internal EscPosPrinterService? LastTestPrintService { get; private set; }

    /// <summary>Печатает образец на том, что сейчас на экране, а НЕ на сохранённых
    /// настройках. Иначе связка «поменял кодовую страницу → напечатал → посмотрел»
    /// требовала бы сохранения и выхода с экрана, то есть перестала бы быть
    /// проверкой этого выбора.
    ///
    /// Команда живёт на SettingsViewModel, а не на строке принтера, ровно как
    /// RemovePrinter рядом: строка не знает про баннеры, а привязка
    /// $parent[UserControl].DataContext с CommandParameter="{Binding}" на этом
    /// экране уже работает.</summary>
    [RelayCommand]
    private async Task TestPrint(PrinterConfigViewModel? printer)
    {
        if (printer == null) return;

        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;

        var service = new EscPosPrinterService(
            printer.ConnectionType,
            printer.ConnectionString,
            printer.SelectedCodePage ?? EscPosCodePages.Default);
        LastTestPrintService = service;

        try
        {
            await service.PrintTestReceiptAsync();
            StatusMessage = I18nService.Instance["TestPrintSent"];
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{I18nService.Instance["TestPrintFailed"]} {ex.Message}";
        }
    }

    /// <summary>Строит дисплей из того, что сейчас в полях, по той же причине.
    /// Одна кнопка, а не по одной на запись: дисплей на кассе один.</summary>
    [RelayCommand]
    private async Task CheckDisplay()
    {
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;

        ICustomerDisplayService display = string.IsNullOrWhiteSpace(CustomerDisplayPort)
            ? new NullCustomerDisplayService()
            : new VfdDisplayService(
                CustomerDisplayPort,
                // Тот же откат, что у Save чуть ниже по файлу, а не свои жёсткие
                // 9600: иначе нечитаемое поле проверяется на одной скорости и
                // сохраняется на другой — «проверка прошла» перестаёт значить
                // что-либо про то, на чём касса в итоге заработает.
                int.TryParse(CustomerDisplayBaudRateText, out var baud) && baud > 0 ? baud : _settingsService.CustomerDisplayBaudRate,
                SelectedDisplayCodePage ?? EscPosCodePages.Default);

        // Своя отсечка по времени. WriteTimeout закрывает запись, но у Open()
        // таймаута нет и SerialPort его не предлагает: зависший драйвер
        // USB-serial держит открытие сколько угодно. Без этого кнопка не
        // отчитается, а повиснет — то есть не сделает того, ради чего заведена.
        var send = display.ShowLineAsync("VV CASH", "Проверка / Test");
        var ok = await Task.WhenAny(send, Task.Delay(3000)) == send && send.Result;

        if (ok) StatusMessage = I18nService.Instance["DisplayCheckOk"];
        else ErrorMessage = I18nService.Instance["DisplayCheckFailed"];
    }

    /// <summary>Confirmation overlay state. This screen is reachable from the login screen,
    /// before anyone has authenticated, and on an offline register a wiped catalog means
    /// nothing can be sold until connectivity returns — so the two destructive buttons no
    /// longer do the work themselves. They arm <see cref="_pendingAction"/> and raise this.
    /// Shaped after PosViewModel's own IsShiftCloseConfirmVisible overlay rather than
    /// inventing a second confirmation pattern.</summary>
    [ObservableProperty] private bool _isConfirmVisible;

    [ObservableProperty] private string _confirmMessage = string.Empty;

    /// <summary>What <see cref="ConfirmCommand"/> will run. Cleared by both exits, so a
    /// stray second tap after a cancel cannot run an action the operator already refused.</summary>
    private Func<Task>? _pendingAction;

    private void AskToConfirm(string message, Func<Task> action)
    {
        _pendingAction = action;
        ConfirmMessage = message;
        IsConfirmVisible = true;
    }

    [RelayCommand]
    private async Task Confirm()
    {
        // Taken and cleared here, before the action runs — not what stops a double tap
        // (that is [RelayCommand]'s default AllowConcurrentExecutions = false on async
        // commands, which drops CanExecute while one is already running), but it is what
        // makes cancel-then-confirm a genuine no-op: nothing is left armed for a stray
        // Confirm to pick up once CancelConfirm has already run.
        var action = _pendingAction;
        _pendingAction = null;
        IsConfirmVisible = false;
        if (action == null) return;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            // The overlay is already down and the operator has no other signal — this
            // screen's own error banner is the one place a failed wipe can still be
            // reported. Silently swallowing it would leave them believing a catalog was
            // cleared that is still there, or half there.
            ErrorMessage = $"{I18nService.Instance["ClearFailed"]} {ex.Message}";
        }
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        _pendingAction = null;
        IsConfirmVisible = false;
    }

    [RelayCommand]
    private void ClearCategories()
        => AskToConfirm(I18nService.Instance["ConfirmClearCategories"], async () =>
        {
            await _offlineStorageService.ClearCategoriesAsync();
            await _offlineStorageService.SetLastSyncVersionAsync(0);
        });

    [RelayCommand]
    private void ClearProducts()
        => AskToConfirm(I18nService.Instance["ConfirmClearProducts"], async () =>
        {
            await _offlineStorageService.ClearProductsAsync();
            await _offlineStorageService.SetLastSyncVersionAsync(0);
        });

    [RelayCommand]
    private void GoBack()    {
        NavigationRequest?.Invoke(_previousViewModel);
    }

    /// <summary>Whether an address is somewhere this register may send its tokens.
    ///
    /// Every call carries the bearer token and the cash token, so plain http would put
    /// both on the wire in the clear. Loopback is the exception — the traffic never
    /// leaves the machine, and running the backend locally is how this gets debugged
    /// on site.
    ///
    /// A bare host ("example.test") is refused rather than silently prefixed: the
    /// register would build request URLs that go nowhere, and that surfaces hours later
    /// as "nothing syncs" rather than here, where it can still be fixed in one keystroke.</summary>
    private static bool IsAcceptableBackendUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }

    [RelayCommand]
    private void Save()
    {
        if (!IsAcceptableBackendUrl(BackendUrl))
        {
            ErrorMessage = "Адрес сервера должен начинаться с https:// (http:// допустим только для localhost).";
            return;
        }
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;

        _settingsService.BackendUrl = BackendUrl;
        _settingsService.CashRegisterToken = CashRegisterToken;
        if (int.TryParse(SyncIntervalText, out int interval) && interval > 0)
        {
            _settingsService.SyncIntervalMinutes = interval;
        }
        else
        {
            _settingsService.SyncIntervalMinutes = 10;
        }

        _settingsService.Language = SelectedLanguage;
        I18nService.Instance.Initialize(SelectedLanguage);

        // Как и с категорией платежа ниже: пустой выбор — это не «формат сбросили»,
        // и записывать его поверх настроенной кассы нельзя.
        if (SelectedPhoneFormat != null)
            _settingsService.PhoneFormatId = SelectedPhoneFormat.Id;

        _settingsService.ReturnOpenCashDrawer = ReturnOpenCashDrawer;
        _settingsService.ReturnPrintReceipt = ReturnPrintReceipt;

        // Пустой порт здесь — не то же самое, что «порт стёрли»: ComboBox мог
        // обнулить CustomerDisplayPort сам, если сохранённое значение не успело
        // попасть в AvailableDisplayPorts (см. конструктор). Как SelectedPhoneFormat
        // и обе категории платежа ниже — пропуск записи, а не запись пустоты поверх
        // настроенного порта.
        if (!string.IsNullOrWhiteSpace(CustomerDisplayPort))
            _settingsService.CustomerDisplayPort = CustomerDisplayPort;
        if (int.TryParse(CustomerDisplayBaudRateText, out var displayBaud) && displayBaud > 0)
            _settingsService.CustomerDisplayBaudRate = displayBaud;
        if (SelectedDisplayCodePage != null)
            _settingsService.CustomerDisplayCodePageId = SelectedDisplayCodePage.Id;

        // Task 24: same cast as the constructor above.
        if (_settingsService is IQueueSettings queueSettings)
        {
            queueSettings.QueueRole = QueueRole;
            queueSettings.QueueServerAddress = QueueServerAddress;
            // Пропуск записи при нечитаемом вводе — тот же приём, что и у
            // SyncIntervalText/CustomerDisplayBaudRateText выше, но без их
            // отката к дефолту: SettingsService.QueuePort уже сам подменяет
            // 0 и отрицательное на DefaultQueuePort (см. его геттер), так
            // что откатывать здесь ещё раз нечего.
            if (int.TryParse(QueuePortText, out var queuePort) && queuePort > 0)
                queueSettings.QueuePort = queuePort;
            queueSettings.QueueSecret = QueueSecret;
            // Не зажимается здесь — IQueueSettings.TillIndex зажимает сам на
            // чтении (0..NumberPool.Tills-1), так что записывать можно как
            // распарсилось.
            if (int.TryParse(TillIndexText, out var tillIndex))
                queueSettings.TillIndex = tillIndex;
        }

        // Only when the list actually loaded: an offline settings visit shows an empty
        // dropdown and a null selection, and writing that through would silently
        // disable exchanges on a register that was configured correctly.
        if (SelectedPaymentCategory != null)
            _settingsService.ExchangePayoutCategoryId = SelectedPaymentCategory.Id;
        if (SelectedReturnPaymentCategory != null)
            _settingsService.ReturnPayoutCategoryId = SelectedReturnPaymentCategory.Id;

        _settingsService.Printers = Printers.Select(p => new PrinterConfig
        {
            Name = p.Name,
            ConnectionType = p.ConnectionType,
            ConnectionString = p.ConnectionString,
            IsEnabled = p.IsEnabled,
            // Здесь ?? Default, а не пропуск записи, как у SelectedPhoneFormat и
            // категорий платежа выше. Причина не в том, что каталог всегда полон —
            // PhoneFormats тоже статичен и сети не требует. Причин три другие:
            // Printers пересобирается списком целиком, поэтому «пропустить и
            // сохранить прежнее» потребовало бы сопоставлять каждую строку с её
            // прежним PrinterConfig, а у только что добавленной строки прежнего нет;
            // и цена промаха здесь несравнима — откат на CP866 это ровно то, что
            // Resolve и так отдаёт ненастроенному принтеру, тогда как пустой формат
            // телефона молча применил бы чужой код страны к настоящим номерам.
            CodePageId = (p.SelectedCodePage ?? EscPosCodePages.Default).Id,
            Roles = p.Roles
        }).ToList();

        _settingsService.Save();

        GoBack();
    }
}
