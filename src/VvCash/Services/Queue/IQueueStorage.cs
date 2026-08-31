using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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
    /// их положили, а не Id, который для этого не годится.</summary>
    Task<IReadOnlyList<(Guid Id, string Payload)>> GetOutboxAsync(string kind);

    Task DeleteOutboxAsync(Guid id);
}
