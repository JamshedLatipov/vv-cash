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
    /// может вовсе не увидеть новую ссылку. Но одного volatile на КАЖДОЕ поле по
    /// отдельности мало: две наложившихся RefreshAsync (фоновый цикл плюс кнопка
    /// "Полная переинициализация" на экране кассы) писали бы Current и Logo
    /// раздельно, и более медленная могла дозаписать Logo от своего прогона
    /// поверх Current от чужого — комбинация, которой на сервере никогда не
    /// существовало. Один volatile на неизменяемую пару чинит и видимость, и
    /// этот рассинхрон разом.</summary>
    private sealed record Snapshot(ReceiptTemplate Template, string Logo);

    private static readonly Snapshot DefaultSnapshot = new(ReceiptTemplate.Default, string.Empty);

    private volatile Snapshot _snapshot = DefaultSnapshot;

    /// <summary>Сериализует сами обновления — тем же приёмом, что
    /// CompositePrinterService._rebuildGate. Смешанную пару выше это не лечит
    /// второй раз (снимок один и неизменяемый, значит она и так невозможна) —
    /// без семафора два наложившихся RefreshAsync просто читали бы SQLite
    /// вперемешку без всякой пользы, и результат определяло бы, какой из них
    /// дописал последним, а не какой реально свежее.</summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>Once true, единственный удавшийся когда-либо RefreshAsync уже
    /// был, и очередной сбой больше не имеет права стирать его результат
    /// дефолтом — см. комментарий RefreshAsync.</summary>
    private volatile bool _hasLoaded;

    public ReceiptTemplateService(IOfflineStorageService storage) => _storage = storage;

    public ReceiptTemplate Current => _snapshot.Template;

    public string Logo => _snapshot.Logo;

    /// <summary>Читает шаблон и логотип из кэша как одну операцию. Три отдельных
    /// дефекта из ревью Task 10 закрыты здесь разом:
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
    /// шаблону отношения не имела.
    ///
    /// 3. Сбой оставляет прежний снимок как есть, а не дефолт, — тот же принцип
    /// "любая беда оставляет закэшированное", что у SyncService и у состава
    /// принтеров, — и откатывается на дефолт только если вообще ничего ни разу не
    /// загрузилось (см. _hasLoaded, тот же приём, что HasLoaded у
    /// CashFeatureService). Иначе работающая касса деградировала бы до
    /// дефолтного чека на первом же временном сбое — том самом, который весь
    /// остальной код этой задачи специально учит переживать.</summary>
    public async Task RefreshAsync()
    {
        await _refreshGate.WaitAsync();
        try
        {
            try
            {
                await _storage.InitializeAsync();

                var rawTemplate = await _storage.GetReceiptTemplateAsync();
                var rawLogo = await _storage.GetReceiptLogoAsync();

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
