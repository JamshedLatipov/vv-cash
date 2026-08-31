using System;
using System.Globalization;
using System.Linq;
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
    /// индексом (Number % Tills). Internal, а не private: SettingsService клэмпит
    /// TillIndex этим же значением — держать два числа в согласии руками означало
    /// бы, что при рассинхроне номера просто начинают выдаваться на две кассы
    /// сразу, без единой ошибки на этот счёт.
    ///
    /// Менять на живой точке нельзя: срез на сегодня уже выдан каждой кассе и
    /// зашит в её NumberPool до конца дня (см. EnsureTodaysPoolAsync), поэтому
    /// смена этого числа посреди смены сталкивает срезы касс до следующей смены
    /// дня, а не применяется сразу. Значение не должно становиться настройкой,
    /// которую можно поменять на бегу.</summary>
    internal const int Tills = 5;

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
    /// одного процесса — это покрывает единственный сценарий, который кассе
    /// вообще нужен: одна касса, один живой экземпляр NumberPool. Два экземпляра
    /// над одним файлом этим семафором не защищены (в проде это и не сценарий —
    /// у каждой кассы свой файл, см. класс-докстринг), но это не тихая порча
    /// данных: вторая транзакция, которой нужна блокировка на запись, которую уже
    /// держит первая, получает от SQLite отказ сразу — "database is locked" — а не
    /// проходит с устаревшим прочитанным значением.</summary>
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

            // Не новая выдача, а отметка на уже текущей: возврат сам по себе не
            // событие в очереди выдачи, он лишь ставит номеру таймер кулдауна
            // относительно того значения seq, что уже есть.
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

    /// <summary>Порядок предпочтения, как задано: нетронутый номер первым, затем
    /// возвращённый и отстоявший кулдаун, и только если нет ни того ни другого —
    /// номер, выданный раньше всех прочих. Третья ветка — то, что не даёт кассе
    /// без экрана на кухне встать намертво: там никто ничего не возвращает,
    /// и без неё первые две ветки голодали бы вечно.
    ///
    /// У третьей ветки есть обратная сторона: если выдать все 180 номеров и
    /// затем вернуть все 180 (вырожденный случай, а не обычная смена), она
    /// готова тут же выдать номер, отпущенный секунду назад — условие «не
    /// раньше кулдауна» у неё не проверяется вовсе, потому что оно относится
    /// только ко второй ветке. Это не дефект: третья ветка существует ради
    /// того, чтобы касса не встала, а не ради кулдауна, и в такой момент
    /// свежих и отстоявших номеров всё равно нет ни одного.</summary>
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

    /// <summary>0, если строки IssueSeq ещё нет или её не удалось разобрать.
    /// EnsureTodaysPoolAsync всегда пишет эту строку при заведении дня, так что
    /// на практике это откат не срабатывает — но если бы он сработал посреди
    /// дня (строку стёрли или испортили руками), последствия не «начали
    /// заново», а тихая порча кулдауна до конца дня: seq снова пойдёт от
    /// маленьких чисел, «$seq - ReleasedAtSeq» уйдёт в минус для уже
    /// освобождённых номеров, и вторая ветка перестанет находить кандидатов,
    /// пока seq не догонит прежние значения ReleasedAtSeq.</summary>
    private static async Task<long> ReadSeqAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Value FROM QueueState WHERE Key = 'IssueSeq'";

        var result = await command.ExecuteScalarAsync();
        return result is string raw
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : 0L;
    }

    private static async Task WriteSeqAsync(SqliteConnection connection, SqliteTransaction transaction, long seq)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO QueueState (Key, Value) VALUES ('IssueSeq', $Value)
            ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;
        ";
        command.Parameters.AddWithValue("$Value", seq.ToString(CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Перешаффливает и обнуляет пул при первой выдаче этой кассы в новый
    /// день. Местное время, как в спецификации: граница дня — это граница смены в
    /// торговом зале, а не часовой пояс сервера, которого может и не быть на связи.
    /// Удаление и вставка — в одной транзакции, чтобы падение посреди перешаффла
    /// не могло оставить таблицу наполовину старой, наполовину новой.</summary>
    private async Task EnsureTodaysPoolAsync(SqliteConnection connection)
    {
        var today = _now().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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

    /// <summary>Срез этой кассы, перемешанный Фишером — Йетсом на потоке
    /// QueueShuffleKeystream (день, индекс кассы, общий секрет — см. его
    /// докстринг о том, почему не System.Random). Детерминированно по дню, так
    /// что перезапуск посреди дня (TheShuffleIsStableAcrossRestartsWithinADay)
    /// воспроизводит тот же порядок, а не теряет, что уже роздано; на секрете,
    /// а не только на дате, чтобы порядок нельзя было предсказать по одному
    /// талону и дате на нём.</summary>
    private int[] ShuffledSlice(string day)
    {
        var slice = Enumerable.Range(FirstNumber, LastNumber - FirstNumber + 1)
            .Where(n => n % Tills == _tillIndex)
            .ToArray();

        var keystream = new QueueShuffleKeystream(day, _tillIndex, _secret);
        for (var i = slice.Length - 1; i > 0; i--)
        {
            var j = keystream.NextIndex(i + 1);
            (slice[i], slice[j]) = (slice[j], slice[i]);
        }

        return slice;
    }
}
