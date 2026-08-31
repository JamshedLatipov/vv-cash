using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
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

                CREATE TABLE IF NOT EXISTS QueueOrders (
                    Id TEXT PRIMARY KEY,
                    Number INTEGER NOT NULL,
                    TillIndex INTEGER NOT NULL,
                    State TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    ReadyAt TEXT,
                    ClosedAt TEXT,
                    SaleDocumentNumber TEXT,
                    Lines TEXT NOT NULL
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

    public async Task<IReadOnlyList<QueueOrder>> GetOrdersAsync()
    {
        await InitializeAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = $"{OrderColumnsSelect} ORDER BY CreatedAt";

        var result = new List<QueueOrder>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(ReadOrder(reader));
        }
        return result;
    }

    public async Task SaveOrderAsync(QueueOrder order)
    {
        await InitializeAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        // DO NOTHING, не UPDATE — см. докстринг на IQueueStorage.SaveOrderAsync:
        // повторно присланная копия не должна откатывать заказ, ушедший вперёд
        // по состояниям, обратно в New.
        command.CommandText = @"
            INSERT INTO QueueOrders
                (Id, Number, TillIndex, State, CreatedAt, ReadyAt, ClosedAt, SaleDocumentNumber, Lines)
            VALUES
                ($Id, $Number, $TillIndex, $State, $CreatedAt, $ReadyAt, $ClosedAt, $SaleDocumentNumber, $Lines)
            ON CONFLICT(Id) DO NOTHING;
        ";
        BindOrder(command, order);

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

    public async Task CloseStaleOrdersAsync(DateTime today)
    {
        await InitializeAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // Кандидаты — всё ещё рабочее состояние. Closed и Cancelled — уже
        // конечные исходы (см. докстринг интерфейса) и здесь не трогаются
        // вовсе, поэтому даже не выбираются.
        using var selectCommand = connection.CreateCommand();
        selectCommand.CommandText =
            $"{OrderColumnsSelect} WHERE State NOT IN ($Closed, $Cancelled)";
        selectCommand.Parameters.AddWithValue("$Closed", QueueOrderState.Closed.ToString());
        selectCommand.Parameters.AddWithValue("$Cancelled", QueueOrderState.Cancelled.ToString());

        // Отбор по календарному дню — в C#, на уже распарсенном DateTime, а
        // не в SQL: CreatedAt на диске несёт тот Kind/оффсет, с которым его
        // записал BindOrder (обычно местное время кассы — см. докстринг
        // интерфейса), а SQL-функция date() сначала нормализует строку к UTC
        // по этому оффсету и лишь потом берёт календарную дату. Для заказа,
        // пробитого рано утром по местному времени с положительным оффсетом,
        // это два разных дня.
        var stale = new List<QueueOrder>();
        using (var reader = await selectCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var order = ReadOrder(reader);
                if (order.CreatedAt.Date < today.Date)
                {
                    stale.Add(order);
                }
            }
        }

        if (stale.Count == 0) return;

        // Одна команда на всех: State и ClosedAt одинаковы для каждой строки
        // (см. докстринг интерфейса — ClosedAt = today, тот же today, что и
        // граница календарного дня выше), меняется только $Id, поэтому
        // параметры заводятся один раз до цикла, а не на каждой итерации.
        using var updateCommand = connection.CreateCommand();
        updateCommand.CommandText = @"
            UPDATE QueueOrders
            SET State = $State, ClosedAt = $ClosedAt
            WHERE Id = $Id;
        ";
        updateCommand.Parameters.AddWithValue("$State", QueueOrderState.Closed.ToString());
        updateCommand.Parameters.AddWithValue("$ClosedAt", FormatDate(today));
        var idParam = updateCommand.Parameters.Add("$Id", SqliteType.Text);

        foreach (var order in stale)
        {
            idParam.Value = order.Id.ToString();
            await updateCommand.ExecuteNonQueryAsync();
        }
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
