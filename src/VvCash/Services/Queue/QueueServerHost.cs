using System;
using System.Threading;
using System.Threading.Tasks;

namespace VvCash.Services.Queue;

/// <summary>Владеет жизненным циклом QueueServer и QueueFlushLoop и держит их в
/// согласии с IQueueSettings.QueueRole всякий раз, когда меняются настройки —
/// тот же приём SettingsChanged, что уже применяет CompositePrinterService для
/// состава принтеров (см. её docstring на _rebuildGate и на порядок отписки
/// после публикации — рассуждение ниже сознательно на него опирается, хотя сам
/// код и не пересекается: там пересборка синхронна и лока обычного lock
/// хватает, здесь старт и остановка Kestrel — это await, который под lock не
/// компилируется).
///
/// До этого класса QueueServer поднимался ровно один раз, в App.axaml.cs, и
/// только если роль этой кассы УЖЕ была Server на старте процесса. Администратор,
/// который открывает экран настроек и включает роль Server на уже работающей
/// кассе, получал тишину: порт не открывался, /kds и /board не грузились, а
/// единственным выходом было закрыть и снова открыть кассу. Этот класс — тот
/// самый живой владелец, которого не хватало: он подписывается на
/// SettingsChanged один раз при создании (в DI — на старте процесса) и с этого
/// момента сверяет и QueueServer, и QueueFlushLoop с текущей ролью на каждое
/// сохранение экрана настроек, а не только на старте.</summary>
public class QueueServerHost
{
    private readonly ISettingsService _settingsService;
    private readonly IQueueStorage _storage;
    private readonly QueueFlushLoop _flushLoop;

    /// <summary>Фабрика QueueServer — тестовый шов, тем же приёмом, что
    /// CompositePrinterService._factory: без него состав того, что тест видит
    /// поднятым, подменить нечем. Продакшн-код (DI, см. App.axaml.cs) его не
    /// передаёт и получает обычное создание QueueServer без изменений.</summary>
    private readonly Func<IQueueStorage, int, string, QueueServer> _serverFactory;

    /// <summary>Сериализует реконсиляции друг относительно друга — тот же
    /// смысл, что у CompositePrinterService._rebuildGate, но асинхронный: старт
    /// и остановка Kestrel — это await, а держать асинхронный код под обычным
    /// lock нельзя (await внутри lock не компилируется). SettingsChanged
    /// прилетает на UI-потоке из Save() на экране настроек, и обработчик не
    /// должен блокировать этот поток, пока порт открывается или сокет
    /// дренируется — поэтому OnSettingsChanged ниже только заводит задачу и
    /// сразу возвращает управление UI-потоку (async void с await внутри, не
    /// .Wait()/.Result), а сериализация двух наложившихся сохранений происходит
    /// здесь, внутри ReconcileAsync, а не в обработчике события. Второе
    /// сохранение, наложившееся на первое, ждёт своей очереди и затем читает
    /// НАСТОЯЩИЕ текущие настройки заново — а не то, что было на момент, когда
    /// оно встало в очередь, — так что после серии быстрых сохранений сервер
    /// всегда сходится к состоянию, которое просит ПОСЛЕДНЕЕ из них, и никогда
    /// не остаётся ни с двумя слушающими портами, ни с осиротевшим Kestrel,
    /// которого некому было остановить.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private QueueServer? _server;

    /// <summary>Порт и секрет, с которыми СЕЙЧАС реально запущен _server — не
    /// то, что просят текущие настройки, а то, с чем последний раз реально
    /// стартовали успешно. Сравнение «нужен ли рестарт» идёт против этих полей,
    /// а не против значения, которое отдаёт IQueueSettings прямо сейчас: иначе
    /// сохранение из другой части экрана настроек (принтер, язык, номер кассы)
    /// каждый раз отскакивало бы работающий сервер и рвало все подключённые
    /// экраны кухни и табло, хотя ни порт, ни секрет не менялись вовсе.</summary>
    private int _runningPort;
    private string _runningSecret = string.Empty;

    /// <summary>Порт и секрет последней ПОПЫТКИ старта — успешной или нет,
    /// отдельно от _runningPort/_runningSecret выше, которые говорят только про
    /// успех. Без этой пары неудачная попытка (например, порт занят другим
    /// процессом) повторялась бы на каждое несвязанное сохранение — экран
    /// настроек заново дёргал бы Kestrel при каждой правке принтера, хотя
    /// ничего из того, что могло бы починить прошлый отказ, не поменялось.</summary>
    private int? _attemptedPort;
    private string? _attemptedSecret;

    private bool _flushRunning;

    /// <summary>Причина последнего неудачного старта, или null, если последняя
    /// попытка была успешной либо эта касса не Server вовсе. Экран настроек
    /// читает её отсюда напрямую при каждом открытии (см. App.axaml.cs) — не из
    /// значения, замороженного один раз при старте процесса, как было раньше.
    /// volatile по той же причине, что и CompositePrinterService._printers:
    /// присваивание ссылки атомарно (ECMA-335), но без volatile UI-поток не
    /// гарантированно увидит свежее значение, записанное фоновой задачей
    /// реконсиляции — атомарности достаточно, чтобы не увидеть половину
    /// строки, но не достаточно, чтобы гарантированно увидеть новую.</summary>
    private volatile string? _lastError;

    public string? LastError => _lastError;

    /// <summary>Порт, на котором реально слушает текущий сервер — null, если
    /// эта касса не Server вовсе или последняя попытка провалилась. internal:
    /// только для тестов, которым нужно постучаться в реально открытый порт
    /// (в частности, когда настройки просят порт 0 — «любой свободный», см.
    /// QueueServerTest). BoardUrl/KdsUrl на экране настроек по-прежнему строятся
    /// из сохранённых QueuePort/QueueSecret, а не отсюда — см. их собственный
    /// docstring в SettingsViewModel.</summary>
    internal int? CurrentPort { get; private set; }

    public QueueServerHost(
        ISettingsService settingsService,
        IQueueStorage storage,
        IQueueClient queueClient,
        Func<IQueueStorage, int, string, QueueServer>? serverFactory = null)
    {
        _settingsService = settingsService;
        _storage = storage;
        _flushLoop = new QueueFlushLoop(queueClient);
        _serverFactory = serverFactory ?? ((s, port, secret) => new QueueServer(s, port, secret));

        _settingsService.SettingsChanged += OnSettingsChanged;

        // Стартовая сверка. Fire-and-forget, тем же решением, что раньше жило
        // прямо в App.axaml.cs (см. QueueServer.StartAsync — оно само ловит
        // свой отказ и не бросает наружу): занятый порт или пустой секрет —
        // это неверная настройка точки, а не повод задержать открытие кассы
        // ожиданием, пока поднимется Kestrel.
        _ = ReconcileAsync();
    }

    private async void OnSettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            await ReconcileAsync();
        }
        catch (Exception ex)
        {
            // Реконсиляция сама не должна уронить обработчик события — тот же
            // принцип, что и у остального оборудования в этом приложении (см.
            // QueueFlushLoop.Start): залогировать и продолжить, а не бросить
            // необработанным на UI-поток.
            Console.WriteLine($"[QueueServerHost] Reconcile failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Сверяет QueueServer и QueueFlushLoop с текущими настройками.
    /// internal, а не private, чтобы конструктор и OnSettingsChanged были не
    /// единственными вызывающими — но тесты жизненного цикла нарочно НЕ зовут
    /// её напрямую после Save(): звать её отсюда значило бы проверять только
    /// то, что сама реконсиляция сходится к правильному состоянию, если её
    /// позвать, — а не то, что SettingsChanged действительно её звёт. Тесты
    /// ждут эффекта через WaitForIdleAsync ниже, который сам не делает ничего,
    /// кроме ожидания уже идущей работы, — так что тест, отключивший подписку
    /// на SettingsChanged, красен, а не зелен по случайности.</summary>
    internal async Task ReconcileAsync()
    {
        await _gate.WaitAsync();
        try
        {
            // IQueueSettings, не пять отдельных полей — SettingsService
            // реализует оба интерфейса на одном объекте (см. её собственный
            // класс), то же приведение, что уже делают SettingsViewModel и
            // App.axaml.cs.
            var settings = (IQueueSettings)_settingsService;
            ReconcileFlushLoop(settings.QueueRole);
            await ReconcileServerAsync(settings.QueueRole, settings.QueuePort, settings.QueueSecret);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ждёт, пока улягутся все реконсиляции, уже идущие или заведённые
    /// прямо сейчас, — не проводя ни одной своей. Берёт _gate и тут же
    /// отпускает: если реконсиляция уже идёт (например, ту, что завёл
    /// OnSettingsChanged после Save() на экране настроек), это ожидание
    /// встаёт за ней и возвращается только когда та закончится; если ничего не
    /// идёт — возвращается сразу же. Только для тестов: продакшн-коду не за
    /// чем ждать этого явно, каждое SettingsChanged само заводит свою задачу.
    ///
    /// Именно поэтому тесты жизненного цикла ждут эффект Save() через этот
    /// метод, а не через прямой вызов ReconcileAsync() выше: тот сам провёл
    /// бы реконсиляцию, даже если бы подписка на SettingsChanged была
    /// сломана или вовсе отсутствовала, — и тест остался бы зелёным на
    /// сломанном коде. Этот метод не делает ничего, кроме ожидания, поэтому
    /// красен именно тогда, когда должен быть красен.
    ///
    /// Надёжность не на честном слове: SettingsChanged — обычное
    /// многоадресное событие, Invoke не возвращается вызывающему (Save()),
    /// пока синхронная часть OnSettingsChanged не дойдёт до первого
    /// НАСТОЯЩЕГО приостановления — а первая же строчка ReconcileAsync это
    /// await _gate.WaitAsync(), которое при свободном семафоре завершается
    /// синхронно, тут же уменьшая счётчик. Значит к моменту, когда Save()
    /// вернёт управление тесту, семафор уже захвачен, если реконсиляция
    /// вообще была заведена, — и последующий WaitForIdleAsync() не проскочит
    /// мимо неё вслепую.</summary>
    internal async Task WaitForIdleAsync()
    {
        await _gate.WaitAsync();
        _gate.Release();
    }

    /// <summary>Цикл досылки буфера следует роли ровно так же, как сегодня
    /// решает App.axaml.cs при старте (см. его прежние remarks) — работает,
    /// пока роль не Off, независимо от того, сервер эта касса или клиент:
    /// сервер тоже ходит к себе по HttpQueueTransport (см. remarks в
    /// ConfigureServices про 127.0.0.1) и его исходящий буфер тоже нужно
    /// дренировать. QueueFlushLoop.Start() безопасно звать повторно (сам гасит
    /// прежний цикл первым делом — см. его docstring), но здесь всё равно
    /// звана только на переходе Off→не-Off: постоянный ре-старт на каждое
    /// несвязанное сохранение не ломает ничего, но и не даёт ничего, кроме
    /// шанса потерять начатый цикл ожидания Task.Delay ровно на границе.</summary>
    private void ReconcileFlushLoop(QueueRole role)
    {
        var shouldRun = role != QueueRole.Off;
        if (shouldRun && !_flushRunning)
        {
            _flushLoop.Start();
            _flushRunning = true;
        }
        else if (!shouldRun && _flushRunning)
        {
            _flushLoop.Dispose();
            _flushRunning = false;
        }
    }

    private async Task ReconcileServerAsync(QueueRole role, int port, string secret)
    {
        if (role != QueueRole.Server)
        {
            await StopServerAsync();
            // Нечего сообщать: эта касса не пытается быть сервером вовсе — тот
            // же случай, для которого QueueServerError на экране настроек
            // остаётся пустым (см. её собственный docstring в
            // SettingsViewModel).
            _lastError = null;
            _attemptedPort = null;
            _attemptedSecret = null;
            return;
        }

        if (_server != null && port == _runningPort && secret == _runningSecret)
        {
            // Работающий сервер уже поднят ровно с этими значениями —
            // сохранение из другой части экрана настроек не должно рвать
            // подключённые экраны кухни и табло.
            return;
        }

        if (_server == null && _lastError != null && port == _attemptedPort && secret == _attemptedSecret)
        {
            // Прошлая попытка с ЭТИМИ ЖЕ значениями уже провалилась (например,
            // порт занят другим процессом) — пока порт или секрет не
            // изменятся, повторная попытка даст тот же результат, только
            // дороже (полная сборка WebApplicationBuilder ради того же отказа).
            return;
        }

        await StopServerAsync();

        _attemptedPort = port;
        _attemptedSecret = secret;

        var server = _serverFactory(_storage, port, secret);
        var bound = await server.StartAsync();
        if (bound >= 0)
        {
            _server = server;
            _runningPort = port;
            _runningSecret = secret;
            _lastError = null;
            CurrentPort = bound;
        }
        else
        {
            _lastError = server.LastError;
            CurrentPort = null;
        }
    }

    private async Task StopServerAsync()
    {
        if (_server == null) return;
        var server = _server;
        _server = null;
        CurrentPort = null;
        await server.StopAsync();
    }

    /// <summary>Останавливает всё и отписывается от SettingsChanged. Не звана
    /// из App.axaml.cs — то же решение, каким сегодня ни QueueServer, ни
    /// QueueFlushLoop не останавливаются явно при закрытии кассы (см. docstring
    /// QueueFlushLoop о том, почему резкое завершение процесса безопасно для
    /// обоих); ронять уже открытые вебсокеты кухни и табло ради секунд более
    /// плавного выключения кассы того не стоит. internal и только для тестов —
    /// без неё каждый тест этого класса оставлял бы позади себя открытый
    /// слушающий Kestrel-порт до конца всего прогона тестов.</summary>
    internal async Task ShutdownAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _settingsService.SettingsChanged -= OnSettingsChanged;
            await StopServerAsync();
            if (_flushRunning)
            {
                _flushLoop.Dispose();
                _flushRunning = false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
