using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VvCash.Services.Data;
using VvCash.Models;

namespace VvCash.Services.Queue;

/// <summary>SQLite за очередью выдачи талонов и заказов кухни. Своя схема, свой файл
/// — см. doc comment на IQueueStorage. NumberPool ходит в тот же файл напрямую через
/// <see cref="ConnectionString"/>, потому что его SQL (выбор номера с приоритетами)
/// не укладывается в узкий CRUD-интерфейс этого класса.</summary>
public class QueueStorage : IQueueStorage
{
    private readonly string _connectionString;
    private bool _isInitialized = false;

    /// <summary>Тот же приём, что в OfflineStorageService: быстрый путь читает
    /// _isInitialized без блокировки, а под ней флаг проверяется повторно.</summary>
    private readonly SemaphoreSlim _initLock = new(1, 1);

    internal string ConnectionString => _connectionString;

    /// <summary>Создаёт хранилище по стандартному пути. <paramref name="dbPath"/> для
    /// теста или иного расположения — production код и DI получают LocalApplicationData
    /// без изменений.</summary>
    public QueueStorage(string? dbPath = null)
    {
        if (string.IsNullOrEmpty(dbPath))
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appDir = Path.Combine(appDataPath, "VvCash");
            Directory.CreateDirectory(appDir);
            dbPath = Path.Combine(appDir, "queue.db");
        }
        _connectionString = $"Data Source={dbPath}";
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        await _initLock.WaitAsync();
        try
        {
            if (_isInitialized) return;

            await InitializeCoreAsync();

            _isInitialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task InitializeCoreAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // До первой схемной команды: journal_mode нельзя менять внутри
        // транзакции, а CREATE TABLE ниже её открывает. См. SqlitePragmas за
        // тем, почему WAL и почему только здесь.
        await SqlitePragmas.ApplyAsync(connection);

        using var command = connection.CreateCommand();
        command.CommandText = @"
                -- IssuedSeq: на какой по счёту выдаче номер ушёл. NULL — номер свободен.
                -- ReleasedAtSeq: на какой выдаче вернулся. NULL — ни разу не возвращался.
                -- IssuedFor: Guid заказа, которому сейчас выдан номер (текстом). NULL,
                -- когда номер свободен. Это то, по чему ReleaseAsync отличает настоящее
                -- закрытие от стале-заявки на уже переизданный номер — см. докстринг
                -- NumberPool.ReleaseAsync. Добавлена по итогам ревью после IssuedSeq/
                -- ReleasedAtSeq, поэтому и в CREATE TABLE, и отдельным ALTER ниже — на
                -- уже стоящих queue.db этой колонки ещё нет (см. AddColumnIfMissingAsync).
                -- Position — место в перемешанном порядке; именно оно, а не Number,
                -- определяет очерёдность выдачи, и именно поэтому по двум талонам
                -- нельзя посчитать оборот.
                CREATE TABLE IF NOT EXISTS NumberPool (
                    Number INTEGER PRIMARY KEY,
                    Position INTEGER NOT NULL,
                    IssuedSeq INTEGER,
                    ReleasedAtSeq INTEGER,
                    IssuedFor TEXT
                );

                CREATE TABLE IF NOT EXISTS QueueState (
                    Key TEXT PRIMARY KEY,
                    Value TEXT
                );

                -- ReceivedAt: когда ЭТОТ сервер сам сохранил заказ — часы сервера,
                -- не клиента. CreatedAt остаётся временем кассы, пробившей заказ
                -- (это то, что видит повар на экране кухни), но решать, кого
                -- CloseStaleOrdersAsync считает устаревшим, по нему нельзя: касса
                -- с неверными часами тогда закрывала бы СВОИ СОБСТВЕННЫЕ только что
                -- пробитые заказы, а полночь по календарю сервера — ЧУЖИЕ ещё
                -- готовящиеся, независимо от часов. ReceivedAt пишется один раз, при
                -- первой вставке (BindReceivedAt в SaveOrderAsync, под тем же ON
                -- CONFLICT DO NOTHING, что и остальные поля) и никогда не
                -- перезаписывается повторной присылкой того же заказа из буфера.
                CREATE TABLE IF NOT EXISTS QueueOrders (
                    Id TEXT PRIMARY KEY,
                    Number INTEGER NOT NULL,
                    TillIndex INTEGER NOT NULL,
                    State TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    ReadyAt TEXT,
                    ClosedAt TEXT,
                    SaleDocumentNumber TEXT,
                    Lines TEXT NOT NULL,
                    ReceivedAt TEXT
                );

                -- Исходящий буфер кассы-клиента. Тот же смысл, что у
                -- UnsyncedDocuments в offline_data.db, и живёт по тем же правилам —
                -- вплоть до RejectedAt/RejectedReason: NULL значит «ещё в ротации»,
                -- заполненные — «сервер отказал по существу, повтор не поможет»,
                -- строка остаётся на диске для разбора, но больше не отправляется.
                CREATE TABLE IF NOT EXISTS QueueOutbox (
                    Id TEXT PRIMARY KEY,
                    Payload TEXT NOT NULL,
                    Kind TEXT NOT NULL,
                    RejectedAt TEXT,
                    RejectedReason TEXT
                );
            ";

        await command.ExecuteNonQueryAsync();

        // NumberPool существовала до колонки IssuedFor: на уже стоящих у разработчиков
        // и на точках queue.db файлах CREATE TABLE IF NOT EXISTS выше — no-op, колонки
        // как не было, так и нет. EnsureTodaysPoolAsync (NumberPool.cs) не чинит это
        // само собой при смене дня — DELETE+INSERT пересоздают только строки таблицы,
        // не её схему, — так что миграция нужна здесь, на каждой инициализации, тем же
        // приёмом, что OfflineStorageService уже применяет к своим таблицам.
        await AddColumnIfMissingAsync(command, "ALTER TABLE NumberPool ADD COLUMN IssuedFor TEXT;");

        // Same idiom, for QueueOrders.ReceivedAt (see the schema comment above): a
        // queue.db from before this fix predates the column too. Rows written before
        // the migration read back with ReceivedAt NULL — CloseStaleOrdersAsync falls
        // back to CreatedAt for exactly those rows (see its own remarks).
        await AddColumnIfMissingAsync(command, "ALTER TABLE QueueOrders ADD COLUMN ReceivedAt TEXT;");
    }

    /// <summary>Runs one ADD COLUMN, treating "it is already there" as the success it
    /// is. Same idiom as OfflineStorageService.AddColumnIfMissingAsync — first needed in
    /// this file for NumberPool.IssuedFor (see its own remarks in the schema above).
    /// A bare catch-all here would swallow a locked database or a corrupt schema just as
    /// quietly, and the register would carry on to fail later on a read with no
    /// connection to the actual problem — so only "already migrated" is treated as
    /// success; anything else is logged loudly instead.</summary>
    private static async Task AddColumnIfMissingAsync(SqliteCommand command, string alter)
    {
        try
        {
            command.CommandText = alter;
            await command.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (
            ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Already migrated. The expected outcome on every queue.db but a fresh one.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QueueStorage] Migration failed ({alter}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task<string?> GetStateAsync(string key)
    {
        await InitializeAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM QueueState WHERE Key = $Key";
        command.Parameters.AddWithValue("$Key", key);

        var result = await command.ExecuteScalarAsync();
        return result as string;
    }

    public async Task SetStateAsync(string key, string value)
    {
        await InitializeAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO QueueState (Key, Value) VALUES ($Key, $Value)
            ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;
        ";
        command.Parameters.AddWithValue("$Key", key);
        command.Parameters.AddWithValue("$Value", value);

        await command.ExecuteNonQueryAsync();
    }

    public async Task SaveOutboxAsync(Guid id, string kind, string payload)
    {
        await InitializeAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO QueueOutbox (Id, Payload, Kind) VALUES ($Id, $Payload, $Kind)
            ON CONFLICT(Id) DO UPDATE SET Payload=excluded.Payload, Kind=excluded.Kind;
        ";
        command.Parameters.AddWithValue("$Id", id.ToString());
        command.Parameters.AddWithValue("$Payload", payload);
        command.Parameters.AddWithValue("$Kind", kind);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<(Guid Id, string Payload)>> GetOutboxAsync(string kind)
    {
        await InitializeAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Payload FROM QueueOutbox WHERE Kind = $Kind AND RejectedAt IS NULL ORDER BY rowid";
        command.Parameters.AddWithValue("$Kind", kind);

        var result = new List<(Guid Id, string Payload)>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // TryParse, не Parse: строка с нечитаемым Id не должна ронять чтение
            // всей остальной, годной части буфера. Сам испорченный ряд здесь же
            // и остаётся невидимым для FlushAsync — у него, в отличие от плохого
            // Payload, даже нет ключа, по которому его можно было бы убрать.
            if (Guid.TryParse(reader.GetString(0), out var id))
            {
                result.Add((id, reader.GetString(1)));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<Guid>> GetOutboxIdsAsync(string kind)
    {
        await InitializeAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id FROM QueueOutbox WHERE Kind = $Kind AND RejectedAt IS NULL ORDER BY rowid";
        command.Parameters.AddWithValue("$Kind", kind);

        var result = new List<Guid>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // Same TryParse-not-Parse leniency as GetOutboxAsync, for the same reason: an
            // unreadable Id must not take the rest of the (readable) buffer down with it.
            if (Guid.TryParse(reader.GetString(0), out var id))
            {
                result.Add(id);
            }
        }
        return result;
    }

    public async Task<int> GetOutboxCountAsync(string kind)
    {
        await InitializeAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        // Тот же фильтр, что у GetOutboxAsync: отклонённые (RejectedAt не
        // NULL) сервером уже разобраны и не в ротации — считать их "ещё не
        // отправлено" значило бы врать кассиру числом, которое никогда не
        // уменьшится само.
        command.CommandText =
            "SELECT COUNT(*) FROM QueueOutbox WHERE Kind = $Kind AND RejectedAt IS NULL";
        command.Parameters.AddWithValue("$Kind", kind);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task DeleteOutboxAsync(Guid id)
    {
        await InitializeAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM QueueOutbox WHERE Id = $Id";
        command.Parameters.AddWithValue("$Id", id.ToString());

        await command.ExecuteNonQueryAsync();
    }

    public async Task MarkOutboxRejectedAsync(Guid id, string reason)
    {
        await InitializeAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE QueueOutbox
            SET RejectedAt = $RejectedAt, RejectedReason = $Reason
            WHERE Id = $Id;
        ";
        command.Parameters.AddWithValue("$Id", id.ToString());
        command.Parameters.AddWithValue("$RejectedAt", DateTime.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("$Reason", reason ?? string.Empty);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<QueueOrder>> GetLiveOrdersAsync()
    {
        await InitializeAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        // Live = ещё не финальное состояние. Это ровно то, что нужно и кухонному
        // экрану, и табло (оба и так отбрасывают Closed/Cancelled на своей
        // стороне — см. их render()) — отдавать им историю целиком, которую они
        // всё равно сразу выбросят, незачем: это тот самый трафик, который на
        // реальной точке за полгода перерастал в доминирующий (см. докстринг
        // интерфейса). GET /orders без явного ?state= и обе рассылки по /ws
        // (SendOrdersAsync, BroadcastOrdersAsync) идут через этот метод.
        command.CommandText = $"{OrderColumnsSelect} WHERE State NOT IN ($Closed, $Cancelled) ORDER BY CreatedAt";
        command.Parameters.AddWithValue("$Closed", QueueOrderState.Closed.ToString());
        command.Parameters.AddWithValue("$Cancelled", QueueOrderState.Cancelled.ToString());

        var result = new List<QueueOrder>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(ReadOrder(reader));
        }
        return result;
    }

    /// <summary>Насколько недавно закрытые (или отменённые) заказы ещё отдаются
    /// GET /orders?state=Closed — окно, на которое опирается
    /// HttpQueueTransport.GetClosedAsync, чтобы вернуть номера своих закрытых
    /// заказов в пул (см. QueueClient.FlushAsync). Щедро, а не туго: гвардия по
    /// идентичности заказа в NumberPool.ReleaseAsync делает повторный возврат
    /// одного и того же заказа безвредной не-операцией (см. её докстринг), а
    /// значит цена слишком широкого окна — лишние байты в ответе, тогда как
    /// цена слишком узкого — номер, который касса-клиент увидела бы закрытым,
    /// уже вышел из окна к моменту, когда сеть у неё наконец восстановилась
    /// (локалка обычно чинится за секунды — см. спеку, — но не гарантированно),
    /// и его никто не возвращает в пул до конца дня. Сутки перекрывают любой
    /// реалистичный обрыв связи внутри одной смены, а более старые Closed/
    /// Cancelled всё равно ничего не отдают внутри дня — обмен номерами имеет
    /// смысл только внутри него: у каждой кассы свой пул перемешивается заново
    /// при первой продаже нового дня (см. NumberPool.EnsureTodaysPoolAsync), так
    /// что заказ, закрытый вчера, никакой кассе сегодня уже не интересен.</summary>
    internal static readonly TimeSpan RecentlyClosedWindow = TimeSpan.FromHours(24);

    public async Task<IReadOnlyList<QueueOrder>> GetRecentlyClosedOrdersAsync(DateTime now)
    {
        await InitializeAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        // Тот же приём, что и в CloseStaleOrdersAsync (см. его докстринг): окно
        // сравнивается в C#, на уже распарсенном DateTime, а не в SQL через
        // date()/строковое сравнение — тот же риск с Kind/оффсетом. SQL здесь
        // сужает только до Closed/Cancelled, и это уже большая часть работы:
        // после PurgeOldClosedOrdersAsync (см. его докстринг) старше недели
        // такие строки просто не существуют, так что этот запрос никогда не
        // читает историю точки целиком, сколько бы она ни проработала.
        command.CommandText = $"{OrderColumnsSelect} WHERE State IN ($Closed, $Cancelled) ORDER BY CreatedAt";
        command.Parameters.AddWithValue("$Closed", QueueOrderState.Closed.ToString());
        command.Parameters.AddWithValue("$Cancelled", QueueOrderState.Cancelled.ToString());

        var cutoff = now - RecentlyClosedWindow;
        var result = new List<QueueOrder>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var order = ReadOrder(reader);
            // ClosedAt отсутствовать не должно у Closed/Cancelled (оба перехода
            // штампуют его — см. QueueServer), но заказ без него — не повод
            // падать: тише включить в выдачу, чем потерять чей-то возврат номера.
            if (order.ClosedAt is not DateTime closedAt || closedAt >= cutoff)
            {
                result.Add(order);
            }
        }
        return result;
    }

    /// <summary>Сколько держать Closed/Cancelled заказ на диске, прежде чем
    /// PurgeOldClosedOrdersAsync сотрёт его насовсем. Больше, чем
    /// RecentlyClosedWindow выше — с большим запасом, а не впритык: если бы
    /// период хранения совпадал с окном выдачи, заказ мог бы исчезнуть из-под
    /// кассы-клиента ровно в момент, когда та наконец опросила сервер после
    /// долгого обрыва. Неделя — не подбор по месту: этого с огромным запасом
    /// хватает на любой реальный обрыв связи внутри одной смены (см.
    /// RecentlyClosedWindow), но при этом файл БД не растёт бесконечно — при
    /// 300 заказах в день это около двух тысяч Closed/Cancelled строк в
    /// худшем случае, а не вся история точки.</summary>
    internal static readonly TimeSpan ClosedOrderRetention = TimeSpan.FromDays(7);

    public async Task PurgeOldClosedOrdersAsync(DateTime now)
    {
        await InitializeAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var selectCommand = connection.CreateCommand();
        selectCommand.CommandText = "SELECT Id, ClosedAt FROM QueueOrders WHERE State IN ($Closed, $Cancelled)";
        selectCommand.Parameters.AddWithValue("$Closed", QueueOrderState.Closed.ToString());
        selectCommand.Parameters.AddWithValue("$Cancelled", QueueOrderState.Cancelled.ToString());

        var cutoff = now - ClosedOrderRetention;
        var deadIds = new List<string>();
        using (var reader = await selectCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                // Без ClosedAt (не должно случаться у Closed/Cancelled — см.
                // GetRecentlyClosedOrdersAsync) возраст не оценить — оставляем
                // строку как есть, а не удаляем по недоказанному предположению.
                if (reader.IsDBNull(1)) continue;
                var closedAt = ParseDate(reader.GetString(1))!.Value;
                if (closedAt < cutoff)
                {
                    deadIds.Add(reader.GetString(0));
                }
            }
        }

        if (deadIds.Count == 0) return;

        using var transaction = connection.BeginTransaction();

        using var deleteCommand = connection.CreateCommand();
        deleteCommand.Transaction = transaction;
        deleteCommand.CommandText = "DELETE FROM QueueOrders WHERE Id = $Id";
        var idParam = deleteCommand.Parameters.Add("$Id", SqliteType.Text);

        foreach (var id in deadIds)
        {
            idParam.Value = id;
            await deleteCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task SaveOrderAsync(QueueOrder order, DateTime receivedAt)
    {
        await InitializeAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        // DO NOTHING, не UPDATE — см. докстринг на IQueueStorage.SaveOrderAsync:
        // повторно присланная копия не должна откатывать заказ, ушедший вперёд
        // по состояниям, обратно в New. Это же и защищает ReceivedAt: повторная
        // присылка того же Id несёт новый $ReceivedAt (время ЭТОГО вызова), но
        // DO NOTHING не даёт ему затереть то время, что сервер записал при
        // первой вставке — а именно оно и есть «когда сервер впервые увидел
        // этот заказ», на чём стоит CloseStaleOrdersAsync.
        command.CommandText = @"
            INSERT INTO QueueOrders
                (Id, Number, TillIndex, State, CreatedAt, ReadyAt, ClosedAt, SaleDocumentNumber, Lines, ReceivedAt)
            VALUES
                ($Id, $Number, $TillIndex, $State, $CreatedAt, $ReadyAt, $ClosedAt, $SaleDocumentNumber, $Lines, $ReceivedAt)
            ON CONFLICT(Id) DO NOTHING;
        ";
        BindOrder(command, order);
        command.Parameters.AddWithValue("$ReceivedAt", receivedAt.ToString("o"));

        await command.ExecuteNonQueryAsync();
    }

    public async Task<QueueOrder?> GetOrderAsync(Guid id)
    {
        await InitializeAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = $"{OrderColumnsSelect} WHERE Id = $Id";
        command.Parameters.AddWithValue("$Id", id.ToString());

        using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadOrder(reader) : null;
    }

    public async Task UpdateOrderStateAsync(QueueOrder order)
    {
        await InitializeAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE QueueOrders
            SET State = $State, ReadyAt = $ReadyAt, ClosedAt = $ClosedAt
            WHERE Id = $Id;
        ";
        command.Parameters.AddWithValue("$Id", order.Id.ToString());
        command.Parameters.AddWithValue("$State", order.State.ToString());
        command.Parameters.AddWithValue("$ReadyAt", (object?)FormatDate(order.ReadyAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$ClosedAt", (object?)FormatDate(order.ClosedAt) ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Насколько старым должен быть заказ (по ReceivedAt — часам сервера,
    /// не по CreatedAt клиента, и не по календарной границе), прежде чем
    /// CloseStaleOrdersAsync его закроет. Выбрано намеренно, не подобрано по
    /// месту, и вот почему четыре часа:
    ///
    /// Формат точки — быстрое обслуживание (кофе, выпечка; талон с номером,
    /// бегунок на кухню), а не ресторан с многочасовой подачей: путь заказа от
    /// оплаты до выдачи — минуты, не часы. Даже с большим запасом на затор на
    /// кухне в час пик — это по-прежнему часы, а не сутки, так что четыре часа
    /// не заденут ни один настоящий заказ, который ещё готовится, включая
    /// пробитый под самое закрытие смены (23:58 плюс четыре часа — это глубокая
    /// ночь, до которой ни одна настоящая точка этого профиля не работает).
    ///
    /// В другую сторону: если кухня забыла закрыть заказ, к утру следующей
    /// смены (обычно 8-12 часов спустя) он уже давно за порогом и уходит первым
    /// же вызовом — либо стартом сервера, либо первым POST /orders нового дня —
    /// а не висит занятым весь день, как было бы с более длинным периодом.</summary>
    internal static readonly TimeSpan StaleOrderGracePeriod = TimeSpan.FromHours(4);

    public async Task CloseStaleOrdersAsync(DateTime now)
    {
        await InitializeAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // Кандидаты — всё ещё рабочее состояние. Closed и Cancelled — уже
        // конечные исходы (см. докстринг интерфейса) и здесь не трогаются
        // вовсе, поэтому даже не выбираются. Только Id и два штампа времени —
        // не весь OrderColumnsSelect: решение о устарелости не нуждается ни в
        // Number, ни в Lines, а тянуть и разбирать их для каждой живой строки
        // на каждом POST /orders было бы работой без всякой цели.
        using var selectCommand = connection.CreateCommand();
        selectCommand.CommandText =
            "SELECT Id, CreatedAt, ReceivedAt FROM QueueOrders WHERE State NOT IN ($Closed, $Cancelled)";
        selectCommand.Parameters.AddWithValue("$Closed", QueueOrderState.Closed.ToString());
        selectCommand.Parameters.AddWithValue("$Cancelled", QueueOrderState.Cancelled.ToString());

        // Возраст, не календарная граница — в C#, на уже распарсенном DateTime.
        // Судья — ReceivedAt (когда ЭТОТ сервер сам сохранил заказ), не
        // CreatedAt: то приезжает с кассы-клиента и несёт её собственные,
        // возможно неверные, часы (см. докстринг интерфейса и схему таблицы
        // выше) — судить по нему значило бы, что касса с часами на день назад
        // сама закрывает себе только что пробитые заказы, а не то, что
        // календарная граница сервера сметает ещё готовящееся у всех сразу
        // ровно в полночь. ReceivedAt отсутствует только у строк, записанных до
        // миграции этой колонки (см. AddColumnIfMissingAsync) или руками в
        // обход SaveOrderAsync (как в части тестов) — там честный откат к
        // CreatedAt, то же значение, по которому судила версия до этой правки.
        var staleIds = new List<string>();
        using (var reader = await selectCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var createdAt = ParseDate(reader.GetString(1))!.Value;
                var receivedAt = reader.IsDBNull(2) ? createdAt : ParseDate(reader.GetString(2))!.Value;
                if (now - receivedAt >= StaleOrderGracePeriod)
                {
                    staleIds.Add(reader.GetString(0));
                }
            }
        }

        if (staleIds.Count == 0) return;

        // Одна транзакция на весь пакет: без неё крах посреди рассылки UPDATE'ов
        // (что угодно между первой и последней строкой — не обязательно сбой
        // самого SQLite) оставил бы день рассортированным наполовину — часть
        // вчерашних заказов закрыта, часть всё ещё числится рабочей, и не
        // осталось ни одного признака, где сweep остановился, чтобы доделать
        // его на следующем вызове, а не начать заново с уже закрытыми заказами
        // в списке кандидатов (WHERE State NOT IN исключил бы их и без того,
        // но полагаться на это как на замену транзакции — везение, не гарантия).
        using var transaction = connection.BeginTransaction();

        // Одна команда на всех: State и ClosedAt одинаковы для каждой строки
        // (ClosedAt = now, то же now, что и граница возраста выше), меняется
        // только $Id, поэтому параметры заводятся один раз до цикла, а не на
        // каждой итерации.
        using var updateCommand = connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText = @"
            UPDATE QueueOrders
            SET State = $State, ClosedAt = $ClosedAt
            WHERE Id = $Id;
        ";
        updateCommand.Parameters.AddWithValue("$State", QueueOrderState.Closed.ToString());
        updateCommand.Parameters.AddWithValue("$ClosedAt", FormatDate(now));
        var idParam = updateCommand.Parameters.Add("$Id", SqliteType.Text);

        foreach (var id in staleIds)
        {
            idParam.Value = id;
            await updateCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private const string OrderColumnsSelect =
        "SELECT Id, Number, TillIndex, State, CreatedAt, ReadyAt, ClosedAt, SaleDocumentNumber, Lines FROM QueueOrders";

    private static void BindOrder(SqliteCommand command, QueueOrder order)
    {
        command.Parameters.AddWithValue("$Id", order.Id.ToString());
        command.Parameters.AddWithValue("$Number", order.Number);
        command.Parameters.AddWithValue("$TillIndex", order.TillIndex);
        command.Parameters.AddWithValue("$State", order.State.ToString());
        command.Parameters.AddWithValue("$CreatedAt", order.CreatedAt.ToString("o"));
        command.Parameters.AddWithValue("$ReadyAt", (object?)FormatDate(order.ReadyAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$ClosedAt", (object?)FormatDate(order.ClosedAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$SaleDocumentNumber", order.SaleDocumentNumber ?? string.Empty);
        command.Parameters.AddWithValue("$Lines", JsonSerializer.Serialize(order.Lines));
    }

    private static QueueOrder ReadOrder(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Number = reader.GetInt32(1),
        TillIndex = reader.GetInt32(2),
        State = Enum.Parse<QueueOrderState>(reader.GetString(3)),
        CreatedAt = ParseDate(reader.GetString(4))!.Value,
        ReadyAt = reader.IsDBNull(5) ? null : ParseDate(reader.GetString(5)),
        ClosedAt = reader.IsDBNull(6) ? null : ParseDate(reader.GetString(6)),
        SaleDocumentNumber = reader.GetString(7),
        Lines = JsonSerializer.Deserialize<List<QueueOrderLine>>(reader.GetString(8)) ?? new()
    };

    private static string? FormatDate(DateTime? value) => value?.ToString("o");

    /// <summary>Тот же приём, что OfflineStorageService применяет к ParkedSales.CreatedAt:
    /// "o" на запись и RoundtripKind на чтение — единственная пара в System.Text.Json/
    /// BCL, которая переживает Local/Utc/Unspecified Kind и сотые доли секунды без
    /// потерь и без оглядки на культуру потока, в котором это когда-нибудь прочитают.</summary>
    private static DateTime? ParseDate(string? value) =>
        value == null ? null : DateTime.Parse(value, null, DateTimeStyles.RoundtripKind);
}
