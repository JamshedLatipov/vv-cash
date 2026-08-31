using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

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
                -- Position — место в перемешанном порядке; именно оно, а не Number,
                -- определяет очерёдность выдачи, и именно поэтому по двум талонам
                -- нельзя посчитать оборот.
                CREATE TABLE IF NOT EXISTS NumberPool (
                    Number INTEGER PRIMARY KEY,
                    Position INTEGER NOT NULL,
                    IssuedSeq INTEGER,
                    ReleasedAtSeq INTEGER
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
}
