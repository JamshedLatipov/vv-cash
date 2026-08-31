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
}
