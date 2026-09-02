using System;
using System.Threading;
using System.Threading.Tasks;
using VvCash.Models.Receipt;
using VvCash.Services.Data;

namespace VvCash.Services;

/// <summary>Устроен как CashFeatureService и по той же причине: касса
/// мид-запуска обязана отрисовать рабочий экран и напечатать чек, а не бросить.</summary>
public class ReceiptTemplateService : IReceiptTemplateService
{
    private readonly IOfflineStorageService _storage;

    /// <summary>Шаблон и логотип как одна неизменяемая пара, а не два независимых
    /// поля. CompositePrinterService читает поставщик шаблона на пути печати, а
    /// пишет сюда фоновый цикл синхронизации — тот же довод, что у
    /// CompositePrinterService._printers про volatile: без него читающий поток
    /// может вовсе не увидеть новую ссылку (это конкретное свойство — видимость
    /// между потоками — воспроизвести юнит-тестом внутри одного процесса нечем;
    /// довод остаётся зафиксирован здесь как документация, не как утверждение,
    /// подкреплённое тестом). Но одного volatile на КАЖДОЕ поле по отдельности
    /// мало: две наложившихся RefreshAsync (фоновый цикл плюс кнопка "Полная
    /// переинициализация" на экране кассы) писали бы Current и Logo раздельно, и
    /// путь печати — единственный настоящий читатель — в _refreshGate ниже не
    /// заходит вовсе, так что семафор его от половинчатого снимка не защищает.
    /// Один volatile на неизменяемую пару, публикуемую одним присваиванием, —
    /// вот что защищает читателя: он либо видит старый снимок целиком, либо
    /// новый целиком, никогда смесь.</summary>
    private sealed record Snapshot(ReceiptTemplate Template, string Logo);

    private static readonly Snapshot DefaultSnapshot = new(ReceiptTemplate.Default, string.Empty);

    private volatile Snapshot _snapshot = DefaultSnapshot;

    /// <summary>Сериализует сами обновления — тем же приёмом, что
    /// CompositePrinterService._rebuildGate. Смешанную пару выше это не лечит:
    /// её чинит атомарность самой публикации (см. Snapshot). Семафор нужен
    /// отдельно — без него два наложившихся RefreshAsync просто читали бы SQLite
    /// вперемешку без всякой пользы, и результат определяло бы, какой из них
    /// дописал последним, а не какой реально свежее.
    ///
    /// RefreshGateTimeout, а не безусловный WaitAsync(): застрявшее обновление
    /// (медленный диск, упавший SQLite-драйвер) иначе ставило бы в очередь все
    /// следующие без права уйти — включая вызов из фонового цикла синхронизации,
    /// который в бою и обнаружил бы затор первым, — и единственным потолком было
    /// бы тридцатисекундное умолчание таймаута команды SQLite. Пять секунд —
    /// щедрый запас над замеренными ~435 мс миграции на холодном старте, но
    /// далеко от получаса. Пропуск, а не ожидание в очереди: следующая
    /// синхронизация по расписанию всё равно перечитает кэш, а вторая копия
    /// того же чтения, стоящая в очереди за первой, смысла не имеет.</summary>
    private static readonly TimeSpan RefreshGateTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>Once true, единственный удавшийся когда-либо RefreshAsync уже
    /// был, и очередной сбой больше не имеет права стирать его результат
    /// дефолтом — см. комментарий RefreshAsync.</summary>
    private volatile bool _hasLoaded;

    public ReceiptTemplateService(IOfflineStorageService storage) => _storage = storage;

    public ReceiptTemplate Current => _snapshot.Template;

    public string Logo => _snapshot.Logo;

    /// <summary>Шаблон и логотип одной парой, за ОДНО чтение <see
    /// cref="_snapshot"/> — в отличие от <see cref="Current"/> и <see
    /// cref="Logo"/> выше, которые каждый читают поле отдельно и потому годны
    /// порознь, но не в паре: вызывающий, которому нужны оба значения одного
    /// поколения (печать — ровно такой вызывающий), не должен собирать их
    /// сам как `(Current, Logo)`. Это ДВА обращения к volatile-полю, и
    /// RefreshAsync, завершившийся между ними, отдал бы половинки разных
    /// поколений — ровно ту рассинхронизацию, от которой защищает снимок
    /// (см. комментарий Snapshot выше), только воспроизведённую снаружи
    /// класса вместо RefreshAsync.
    ///
    /// Читает поле в локальную переменную РОВНО ОДИН раз и берёт оба значения
    /// из неё: снимок неизменяем, так что как только локальная ссылка на
    /// него получена, дальнейшее присваивание _snapshot из RefreshAsync на
    /// уже прочитанную пару не влияет никак.</summary>
    public (ReceiptTemplate Template, string Logo) CurrentTemplateAndLogo
    {
        get
        {
            var snapshot = _snapshot;
            return (snapshot.Template, snapshot.Logo);
        }
    }

    /// <summary>Читает шаблон и логотип из кэша как одну операцию.
    ///
    /// 1. InitializeAsync() вызывается здесь, внутри общего перехвата, а не
    /// отдельной голой строкой в App.axaml.cs. Раньше она стояла без перехвата, и
    /// битая SQLite (оборвавшееся питание кассы — канонический сценарий порчи
    /// файла, который OfflineStorageService прямо документирует как "должен
    /// оставить кассу открытой, а не уронить её") давала необработанное
    /// исключение до создания MainWindow — окно логина не появлялось вовсе.
    ///
    /// 2. Оба сырых значения читаются в локальные переменные до публикации: снимок
    /// собирается и присваивается одним действием, только когда обе операции
    /// удались. Раньше Current обновлялся сразу после разбора шаблона, а Logo —
    /// отдельной следующей строкой; сбой чтения логотипа откатывал перехватом уже
    /// обновлённый (годный!) шаблон обратно на дефолт, хотя причина отказа к
    /// шаблону отношения не имела. Публикация в два шага (сначала Current с новым
    /// шаблоном и старым логотипом, потом с новым логотипом) была бы такой же
    /// дырой для читателя, даже без исключений: путь печати мог бы застать
    /// смешанную пару прямо между двумя присваиваниями.
    ///
    /// 3. Сбой оставляет прежний снимок как есть, а не дефолт, — тот же принцип
    /// "любая беда оставляет закэшированное", что у SyncService и у состава
    /// принтеров, — и откатывается на дефолт только если вообще ничего ни разу не
    /// загрузилось (см. _hasLoaded, тот же приём, что HasLoaded у
    /// CashFeatureService). Иначе работающая касса деградировала бы до
    /// дефолтного чека на первом же временном сбое.
    ///
    /// ConfigureAwait(false) на каждом ожидании: этот метод зовётся блокирующе
    /// (GetAwaiter().GetResult()) с потока интерфейса на старте App.axaml.cs. Пока
    /// семафор свободен, всё заканчивается синхронно и это не имеет значения; но
    /// если он занят (второй RefreshAsync уже отрабатывает — теоретически
    /// возможно, если фоновый цикл синхронизации успел поднять ProductsSynced
    /// одновременно со стартом), продолжение без ConfigureAwait(false) ушло бы
    /// обратно в захваченный контекст, а поток интерфейса к этому моменту уже
    /// заблокирован тем же вызовом — под настоящим SynchronizationContext
    /// Avalonia это дедлок. Сегодня недостижимо (второй претендент на семафор
    /// появляется только после логина, когда стартовый вызов уже отработал), но
    /// запас прочности стоит одной строки. Полная страховка потребовала бы того
    /// же и внутри OfflineStorageService (там свой SqliteConnection/await на
    /// каждый вызов) — это осталось не сделано, вне границ этой правки.</summary>
    public async Task RefreshAsync()
    {
        if (!await _refreshGate.WaitAsync(RefreshGateTimeout).ConfigureAwait(false))
        {
            Console.WriteLine(
                "[ReceiptTemplateService] refresh skipped: a previous refresh is still in flight " +
                $"after {RefreshGateTimeout.TotalSeconds}s, the next scheduled sync will retry");
            return;
        }

        try
        {
            try
            {
                await _storage.InitializeAsync().ConfigureAwait(false);

                var rawTemplate = await _storage.GetReceiptTemplateAsync().ConfigureAwait(false);
                var rawLogo = await _storage.GetReceiptLogoAsync().ConfigureAwait(false);

                _snapshot = new Snapshot(ReceiptTemplate.Parse(rawTemplate), rawLogo);
                _hasLoaded = true;
            }
            catch (Exception ex)
            {
                if (!_hasLoaded) _snapshot = DefaultSnapshot;
                Console.WriteLine(
                    $"[ReceiptTemplateService] refresh error: {ex.GetType().Name}: {ex.Message}, " +
                    (_hasLoaded ? "keeping last good template" : "using default template"));
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
