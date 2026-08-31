using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace VvCash.Services.Queue;

/// <summary>Пул трёхзначных номеров для одной кассы. Два требования заказчика:
/// номер не должен выдавать оборот (значит — шаффл, не счётчик), и кассы не должны
/// координироваться по сети (значит — у каждой своя выделенная mod-Tills часть
/// диапазона). Реализация опирается на то, что в проде у каждой кассы свой файл
/// БД (см. QueueStorage) — таблица NumberPool в файле этой кассы никогда не
/// содержит чужих номеров, так что срез не нужно хранить отдельной колонкой.</summary>
public class NumberPool : INumberPool
{
    /// <summary>Количество касс в торговой точке. Номер принадлежит кассе с
    /// индексом (Number % Tills).</summary>
    private const int Tills = 5;

    private const int FirstNumber = 100;
    private const int LastNumber = 999;

    /// <summary>Сколько выдач должно пройти с момента возврата номера, прежде
    /// чем его можно выдать снова. Не время — количество выдач: так граница не
    /// зависит от того, насколько быстро или медленно идёт смена.</summary>
    internal const int CooldownIssues = 50;

    private readonly QueueStorage _storage;
    private readonly int _tillIndex;
    private readonly string _secret;
    private readonly Func<DateTime> _now;

    /// <summary>Сериализует выдачу и возврат друг относительно друга внутри
    /// одного процесса — см. вопрос о конкурентности в отчёте по задаче: два
    /// экземпляра NumberPool над одним файлом (как в тесте на устойчивость
    /// шаффла к перезапуску) этим семафором не защищены, но и не работают
    /// параллельно ни в одном сценарии, который поддерживает эта задача — у
    /// каждой кассы всегда один живой экземпляр над её собственным файлом.</summary>
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public NumberPool(QueueStorage storage, int tillIndex, string secret, Func<DateTime> now)
    {
        _storage = storage;
        _tillIndex = tillIndex;
        _secret = secret;
        _now = now;
    }

    public async Task<int> IssueAsync()
    {
        await _storage.InitializeAsync();

        await _semaphore.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_storage.ConnectionString);
            await connection.OpenAsync();

            await EnsureTodaysPoolAsync(connection);

            using var transaction = connection.BeginTransaction();

            var seq = await ReadSeqAsync(connection, transaction) + 1;

            var number = await SelectNumberToIssueAsync(connection, transaction, seq)
                ?? throw new InvalidOperationException(
                    $"NumberPool for till {_tillIndex} has no numbers to issue — the slice is empty.");

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText =
                    "UPDATE NumberPool SET IssuedSeq = $seq, ReleasedAtSeq = NULL WHERE Number = $n";
                update.Parameters.AddWithValue("$seq", seq);
                update.Parameters.AddWithValue("$n", number);
                await update.ExecuteNonQueryAsync();
            }

            await WriteSeqAsync(connection, transaction, seq);

            await transaction.CommitAsync();
            return number;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task ReleaseAsync(int number)
    {
        await _storage.InitializeAsync();

        await _semaphore.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_storage.ConnectionString);
            await connection.OpenAsync();

            using var transaction = connection.BeginTransaction();

            // Not a new sequence number: a release does not itself count as an
            // event in the issue order, it only timestamps the number's cooldown
            // against whatever the sequence already is.
            var seq = await ReadSeqAsync(connection, transaction);

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText =
                    "UPDATE NumberPool SET IssuedSeq = NULL, ReleasedAtSeq = $seq WHERE Number = $n";
                update.Parameters.AddWithValue("$seq", seq);
                update.Parameters.AddWithValue("$n", number);
                await update.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>Order of preference, exactly as specified: an untouched number
    /// first, then a released one that has cleared the cooldown, and only when
    /// neither exists — the number issued longest ago. That third branch is what
    /// keeps a kitchen-screen-less shift from ever stalling: nothing is ever
    /// released there, so branches 1 and 2 would otherwise starve forever.</summary>
    private static async Task<int?> SelectNumberToIssueAsync(
        SqliteConnection connection, SqliteTransaction transaction, long seq)
    {
        var fresh = await ScalarNumberAsync(connection, transaction, @"
            SELECT Number FROM NumberPool
            WHERE IssuedSeq IS NULL AND ReleasedAtSeq IS NULL
            ORDER BY Position LIMIT 1", null);
        if (fresh.HasValue) return fresh;

        var cooled = await ScalarNumberAsync(connection, transaction, @"
            SELECT Number FROM NumberPool
            WHERE IssuedSeq IS NULL AND ReleasedAtSeq IS NOT NULL
              AND ($seq - ReleasedAtSeq) >= $cooldown
            ORDER BY ReleasedAtSeq LIMIT 1",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$seq", seq);
                cmd.Parameters.AddWithValue("$cooldown", CooldownIssues);
            });
        if (cooled.HasValue) return cooled;

        return await ScalarNumberAsync(connection, transaction, @"
            SELECT Number FROM NumberPool
            ORDER BY COALESCE(IssuedSeq, ReleasedAtSeq, 0) LIMIT 1", null);
    }

    private static async Task<int?> ScalarNumberAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql,
        Action<SqliteCommand>? bind)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        bind?.Invoke(command);

        var result = await command.ExecuteScalarAsync();
        return result == null ? null : Convert.ToInt32(result);
    }

    private static async Task<long> ReadSeqAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Value FROM QueueState WHERE Key = 'IssueSeq'";

        var result = await command.ExecuteScalarAsync();
        return result is string raw && long.TryParse(raw, out var value) ? value : 0L;
    }

    private static async Task WriteSeqAsync(SqliteConnection connection, SqliteTransaction transaction, long seq)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO QueueState (Key, Value) VALUES ('IssueSeq', $Value)
            ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;
        ";
        command.Parameters.AddWithValue("$Value", seq.ToString());
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Reshuffles and resets the pool the first time this till issues on a
    /// new day. Local time, per the spec: the day boundary belongs to the shop
    /// floor, not to whatever time zone a server happens to run in. Delete and
    /// insert run in one transaction so a crash mid-reshuffle can never leave the
    /// table half old, half new.</summary>
    private async Task EnsureTodaysPoolAsync(SqliteConnection connection)
    {
        var today = _now().ToString("yyyy-MM-dd");

        string? storedDay;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT Value FROM QueueState WHERE Key = 'Day'";
            storedDay = (await read.ExecuteScalarAsync()) as string;
        }

        if (storedDay == today) return;

        var slice = ShuffledSlice(today);

        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM NumberPool";
            await delete.ExecuteNonQueryAsync();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO NumberPool (Number, Position, IssuedSeq, ReleasedAtSeq) VALUES ($Number, $Position, NULL, NULL)";
            var numberParam = insert.Parameters.Add("$Number", SqliteType.Integer);
            var positionParam = insert.Parameters.Add("$Position", SqliteType.Integer);

            for (var position = 0; position < slice.Length; position++)
            {
                numberParam.Value = slice[position];
                positionParam.Value = position;
                await insert.ExecuteNonQueryAsync();
            }
        }

        using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = @"
                INSERT INTO QueueState (Key, Value) VALUES ('IssueSeq', '0')
                    ON CONFLICT(Key) DO UPDATE SET Value = '0';
                INSERT INTO QueueState (Key, Value) VALUES ('Day', $Day)
                    ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            ";
            state.Parameters.AddWithValue("$Day", today);
            await state.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>This till's slice of the range, Fisher–Yates shuffled under a seed
    /// derived from the day, the till index and the shared secret. Deterministic
    /// per day so a mid-day restart (TheShuffleIsStableAcrossRestartsWithinADay)
    /// reproduces the same order instead of losing track of what was already
    /// handed out; seeded from the secret, not just the date, so the order cannot
    /// be predicted from a ticket and the day alone.</summary>
    private int[] ShuffledSlice(string day)
    {
        var slice = Enumerable.Range(FirstNumber, LastNumber - FirstNumber + 1)
            .Where(n => n % Tills == _tillIndex)
            .ToArray();

        var seed = BitConverter.ToInt32(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{day}|{_tillIndex}|{_secret}")), 0);
        var random = new Random(seed);

        for (var i = slice.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (slice[i], slice[j]) = (slice[j], slice[i]);
        }

        return slice;
    }
}
