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

    /// <summary>Все заказы сервера, старые первыми — то, что отдаёт GET /orders
    /// кухне и табло целиком, поэтому сортировка здесь, а не на стороне
    /// вызывающего.</summary>
    Task<IReadOnlyList<QueueOrder>> GetOrdersAsync();

    /// <summary>Кладёт присланный кассой заказ. ON CONFLICT(Id) DO NOTHING, не
    /// UPDATE: касса-клиент досылает буфер, не зная, что из него уже дошло, а
    /// заказ к этому моменту мог успеть продвинуться по состояниям на кухне —
    /// повторно пришедшая копия не должна откатывать его обратно в New.</summary>
    Task SaveOrderAsync(QueueOrder order);

    /// <summary>Один заказ по Id, или null, если такого нет — 404 у эндпоинта
    /// смены состояния строится на этом null.</summary>
    Task<QueueOrder?> GetOrderAsync(Guid id);

    /// <summary>Переносит State, ReadyAt и ClosedAt из <paramref name="order"/> в
    /// строку с тем же Id. Не общий UPDATE по всем колонкам: единственный
    /// вызывающий — переход состояния, и лишние колонки в запросе только
    /// маскировали бы, что именно он меняет.</summary>
    Task UpdateOrderStateAsync(QueueOrder order);

    /// <summary>Переводит в Closed и штампует ClosedAt = <paramref name="today"/>
    /// каждый заказ, чей CreatedAt приходится на календарный день раньше
    /// <paramref name="today"/>. Только New/InProgress/Ready — уже Closed и
    /// уже Cancelled не трогает: это разные исходы, а не один и тот же, и
    /// заказ, который кухня вчера уже отменила, не должен молча стать
    /// «закрытым по расписанию» — отчёт когда-нибудь спросит про разницу.
    ///
    /// Календарный день — не «прошло 24 часа»: заказ, пробитый в 23:59,
    /// обязан закрыться к утру, а не ровно через сутки. <paramref name="today"/>
    /// служит и «сейчас» для ClosedAt, и границей календарного дня — тем же
    /// Kind/оффсетом, с которым его передаёт вызывающий (обычно
    /// местное время кассы), а не UTC-нормализацией SQL-функции date():
    /// та сперва переводит время по оффсету в UTC и только потом берёт
    /// календарную дату, так что заказ, пробитый рано утром по местному
    /// времени с положительным оффсетом, откатился бы на день назад.</summary>
    Task CloseStaleOrdersAsync(DateTime today);
}
