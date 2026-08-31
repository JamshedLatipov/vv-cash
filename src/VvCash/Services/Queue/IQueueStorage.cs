using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Queue;

/// <summary>SQLite очереди. Отдельный файл, а не таблицы в offline_data.db:
/// схема продаж и схема очереди живут независимо, чистятся в разное время, и
/// два соединения к одному файлу дали бы «database is locked» на ровном месте.</summary>
public interface IQueueStorage
{
    Task InitializeAsync();
    Task<string?> GetStateAsync(string key);
    Task SetStateAsync(string key, string value);

    /// <summary>Кладёт строку буфера (или заменяет уже лежащую с тем же Id —
    /// повторная постановка того же заказа не должна плодить дубли на диске).
    /// Kind — по какому типу записи это (сейчас только заказы; позже сюда же
    /// лягут смены состояния).</summary>
    Task SaveOutboxAsync(Guid id, string kind, string payload);

    /// <summary>Строки буфера одного типа, старые первыми — порядок, в котором
    /// их положили, а не Id, который для этого не годится. Отклонённые
    /// (см. MarkOutboxRejectedAsync) сюда не попадают — тот же приём, что
    /// GetUnsyncedDocumentsAsync применяет к RejectedAt: это то, что ещё нужно
    /// сделать, а не то, что уже попробовали и получили осознанный отказ.</summary>
    Task<IReadOnlyList<(Guid Id, string Payload)>> GetOutboxAsync(string kind);

    /// <summary>Сколько строк буфера данного типа ещё в ротации отправки —
    /// то же множество, что отдаёт GetOutboxAsync, но счётчиком, а не
    /// списком: PosViewModel показывает кассиру именно число, и вытягивать
    /// ради него весь Payload каждой строки незачем.</summary>
    Task<int> GetOutboxCountAsync(string kind);

    Task DeleteOutboxAsync(Guid id);

    /// <summary>Выводит строку буфера из ротации отправки, не удаляя её — как
    /// OfflineStorageService.MarkDocumentRejectedAsync для документов бэкенда.
    /// Для случая, когда повтор не поможет: сервер отказал по существу, а не
    /// был недоступен.</summary>
    Task MarkOutboxRejectedAsync(Guid id, string reason);

    /// <summary>Заказы, ещё не в конечном состоянии (не Closed, не Cancelled),
    /// старые первыми — то, что нужно кухонному экрану и табло: доеденный заказ
    /// не должен быть ни в снимке при переподключении, ни в рассылке по /ws.
    /// Раньше один метод (GetOrdersAsync) отдавал историю точки целиком на
    /// каждый такой запрос — не по дням, не по возрасту, ничего не удалялось —
    /// и это же он отдавал в ответ на КАЖДЫЙ POST /orders и КАЖДЫЙ тап на
    /// экране кухни через BroadcastOrdersAsync; на реальной точке (300
    /// заказов/день) это становилось доминирующим трафиком точки в течение
    /// года. Теперь два раздельных метода под два разных потребителя — этот и
    /// GetRecentlyClosedOrdersAsync ниже.</summary>
    Task<IReadOnlyList<QueueOrder>> GetLiveOrdersAsync();

    /// <summary>Заказы в состоянии Closed или Cancelled, чей ClosedAt не старше
    /// QueueStorage.RecentlyClosedWindow от <paramref name="now"/> — то, что
    /// нужно HttpQueueTransport.GetClosedAsync: касса-клиент опрашивает это
    /// каждые 15 секунд, чтобы вернуть номера своих закрытых заказов в пул (см.
    /// NumberPool.ReleaseAsync). Окно, не история целиком, по той же причине,
    /// что и у GetLiveOrdersAsync — см. QueueStorage.RecentlyClosedWindow за
    /// разбором, почему выбрано именно такое.</summary>
    Task<IReadOnlyList<QueueOrder>> GetRecentlyClosedOrdersAsync(DateTime now);

    /// <summary>Насовсем удаляет Closed/Cancelled заказы старше
    /// QueueStorage.ClosedOrderRetention от <paramref name="now"/> — то, что
    /// GetRecentlyClosedOrdersAsync больше не отдаёт клиентам, не должно расти
    /// в файле бесконечно тоже: без этого сама таблица QueueOrders продолжала
    /// бы копить всё, что через неё прошло, даже если ни один запрос больше не
    /// читает эти строки целиком. New/InProgress/Ready не трогает — только
    /// заказы, уже дошедшие до конечного состояния.</summary>
    Task PurgeOldClosedOrdersAsync(DateTime now);

    /// <summary>Кладёт присланный кассой заказ. ON CONFLICT(Id) DO NOTHING, не
    /// UPDATE: касса-клиент досылает буфер, не зная, что из него уже дошло, а
    /// заказ к этому моменту мог успеть продвинуться по состояниям на кухне —
    /// повторно пришедшая копия не должна откатывать его обратно в New. Тот же
    /// DO NOTHING защищает и <paramref name="receivedAt"/>: он пишется только
    /// при первой вставке этого Id и уже не может быть переписан повторной
    /// присылкой того же заказа с новым временем вызова — см. CloseStaleOrdersAsync
    /// за тем, почему это должно быть время первой попытки, а не последней.</summary>
    Task SaveOrderAsync(QueueOrder order, DateTime receivedAt);

    /// <summary>Один заказ по Id, или null, если такого нет — 404 у эндпоинта
    /// смены состояния строится на этом null.</summary>
    Task<QueueOrder?> GetOrderAsync(Guid id);

    /// <summary>Переносит State, ReadyAt и ClosedAt из <paramref name="order"/> в
    /// строку с тем же Id. Не общий UPDATE по всем колонкам: единственный
    /// вызывающий — переход состояния, и лишние колонки в запросе только
    /// маскировали бы, что именно он меняет.</summary>
    Task UpdateOrderStateAsync(QueueOrder order);

    /// <summary>Переводит в Closed и штампует ClosedAt = <paramref name="now"/>
    /// каждый заказ, чей ReceivedAt (когда ЭТОТ сервер сам сохранил заказ — не
    /// CreatedAt клиента, см. схему QueueOrders в QueueStorage) старше
    /// QueueStorage.StaleOrderGracePeriod. Только New/InProgress/Ready — уже
    /// Closed и уже Cancelled не трогает: это разные исходы, а не один и тот
    /// же, и заказ, который кухня вчера уже отменила, не должен молча стать
    /// «закрытым по расписанию» — отчёт когда-нибудь спросит про разницу.
    ///
    /// Возраст, не календарная граница — было наоборот (закрывался каждый
    /// заказ, чей CreatedAt приходился на день раньше <paramref name="now"/>),
    /// и это ломало сразу две вещи: первая продажа после полуночи закрывала
    /// всё ещё готовящееся с прошлого дня разом (номер уходил обратно в пул и
    /// мог достаться следующему покупателю, пока первый ещё ждёт заказ), а
    /// касса с часами на день назад закрывала СВОИ ЖЕ только что пробитые
    /// заказы через секунды после POST /orders с любой другой кассы — спека
    /// обещает, что расхождение часов между кассами не имеет значения, и это
    /// было правдой для пулов номеров, но не для этого. Часы сервера в
    /// ReceivedAt и щедрый возрастной порог (не календарная полночь) чинят
    /// обе сразу: заказ, пробитый в 23:59, переживает полночь, потому что ему
    /// всего минуту от роду, а не потому что «тот же день».</summary>
    Task CloseStaleOrdersAsync(DateTime now);
}
