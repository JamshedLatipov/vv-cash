using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace VvCash.Services.Data;

/// <summary>Режим журналирования, который обе базы кассы — offline_data.db
/// (<see cref="OfflineStorageService"/>) и queue.db (Services.Queue.QueueStorage) —
/// выставляют себе при инициализации.
///
/// WAL, а не журнал отката по умолчанию, ради одной конкретной вещи. В режиме
/// отката коммит писателя берёт EXCLUSIVE на файл базы, и на это время читатели
/// встают. Писатель у кассы — фоновая синхронизация: SaveProductsAsync заливает
/// весь каталог одной транзакцией. Читатель — UI-поток, и он читает синхронно:
/// у Microsoft.Data.Sqlite методы *Async синхронные (провайдер не умеет
/// асинхронный ввод-вывод, Async там только сигнатура), поэтому заблокированное
/// чтение из ViewModel — это заблокированный UI-поток, а не уступленный. Отсюда
/// и замерзание кассы во время синхронизации. В WAL читатели не блокируют
/// писателя и писатель не блокирует читателей — контакт пропадает целиком.
///
/// synchronous НЕ трогаем и оставляем FULL, хотя NORMAL — обычный спутник WAL и
/// заметно дешевле по fsync. В WAL+NORMAL пропадание питания стоит последних
/// закоммиченных транзакций. На торговой точке это ровно тот сценарий, который
/// случается, и терять там нечего кроме офлайн-документа о продаже, за которую
/// деньги уже взяты.
///
/// Таймаут ожидания блокировки тоже оставлен по умолчанию (30 с у
/// Microsoft.Data.Sqlite, он же CommandTimeout): писателей всё ещё двое —
/// синхронизация и постановка документа в очередь при оплате, — а WAL их между
/// собой не развязывает. Укоротить это ожидание значит превратить медленную
/// продажу в упавшую.</summary>
internal static class SqlitePragmas
{
    /// <summary>Возвращает режим, который база отдала в ответ, — не обязательно
    /// запрошенный. WAL требует разделяемой памяти между процессами и на сетевой
    /// шаре не включается; SQLite в этом случае не падает, а молча остаётся в
    /// прежнем режиме, и без этой проверки такая касса выглядела бы починенной.
    ///
    /// Зовётся из InitializeCoreAsync до первой схемной команды и только оттуда:
    /// journal_mode записан в заголовок файла базы и переживает и соединение, и
    /// перезапуск — выставлять его на каждом открытии соединения незачем.</summary>
    public static async Task<string> ApplyAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        var mode = await command.ExecuteScalarAsync() as string ?? "unknown";

        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                $"[SqlitePragmas] journal_mode stayed '{mode}' for {connection.DataSource} — "
                + "WAL is NOT in effect, readers will still block on the sync writer.");
        }

        return mode;
    }
}
