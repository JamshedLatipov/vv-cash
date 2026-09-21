using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace VvCash.Services.Queue;

/// <summary>Пул номеров для одной кассы. Два требования заказчика: номер не
/// должен выдавать оборот (значит — шаффл, не счётчик; по желанию точки —
/// отключаемый), и кассы не должны координироваться по сети (значит — у каждой
/// своя выделенная часть диапазона, см. QueueNumberSlice). Форма пула —
/// диапазон, число касс, перемешивать или нет — приходит из QueueNumberOptions:
/// снимок берётся на каждой выдаче, а пересборка таблицы привязана к ключу
/// PoolKey, а не только к дню — см. EnsurePoolAsync. Реализация опирается на
/// то, что в проде у каждой кассы свой файл БД (см. QueueStorage) — таблица
/// NumberPool в файле этой кассы никогда не содержит чужих номеров, так что
/// срез не нужно хранить отдельной колонкой.</summary>
public class NumberPool : INumberPool
{
    /// <summary>Сколько выдач должно пройти с момента возврата номера, прежде
    /// чем его можно выдать снова. Не время — количество выдач: так граница не
    /// зависит от того, насколько быстро или медленно идёт смена.</summary>
    internal const int CooldownIssues = 50;

    private readonly QueueStorage _storage;
    private readonly Func<QueueNumberOptions> _options;
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

    /// <summary>options читается на каждой выдаче, а не один раз: экран
    /// настроек сохраняет форму номера, и следующий талон обязан выйти уже
    /// в ней — без перезапуска и без ожидания завтра. Снимок берётся один раз
    /// в начале IssueAsync, под семафором, чтобы PoolKey и срез считались от
    /// одних и тех же значений.</summary>
    public NumberPool(QueueStorage storage, Func<QueueNumberOptions> options, Func<DateTime> now)
    {
        _storage = storage;
        _options = options;
        _now = now;
    }

    public async Task<int> IssueAsync(Guid orderId)
    {
        await _storage.InitializeAsync();

        await _semaphore.WaitAsync();
        try
        {
            var options = _options();

            using var connection = new SqliteConnection(_storage.ConnectionString);
            await connection.OpenAsync();

            await EnsurePoolAsync(connection, options);

            using var transaction = connection.BeginTransaction();

            var seq = await ReadSeqAsync(connection, transaction) + 1;

            var number = await SelectNumberToIssueAsync(connection, transaction, seq)
                ?? throw new InvalidOperationException(
                    $"NumberPool for till {options.TillIndex} has no numbers to issue — the slice is empty.");

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                // IssuedFor — see the class docstring on ReleaseAsync's guard: this is
                // the value that later tells a genuine close of THIS order apart from a
                // stale replay of some earlier order that used to hold the same number.
                update.CommandText =
                    "UPDATE NumberPool SET IssuedSeq = $seq, ReleasedAtSeq = NULL, IssuedFor = $orderId WHERE Number = $n";
                update.Parameters.AddWithValue("$seq", seq);
                update.Parameters.AddWithValue("$n", number);
                update.Parameters.AddWithValue("$orderId", orderId.ToString());
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

    /// <summary>Release guard fixed in this review round. It used to be
    /// <c>WHERE Number = $n AND IssuedSeq IS NOT NULL</c>, on the claim that a stale
    /// release for a number already re-issued to someone else "simply finds no row" —
    /// wrong: a re-issued number still has <c>IssuedSeq IS NOT NULL</c> (it is issued,
    /// just to somebody else), so the stale release matched it and went through anyway.
    ///
    /// Why it fired in a shop: QueueServer's GET /orders returns every order ever
    /// stored — nothing filters by day and nothing ever deletes — so
    /// QueueClient.FlushAsync replays ReleaseAsync for every order this till has ever
    /// closed, every 15 seconds, forever. The very next re-issue of that number gave it
    /// to a new customer; the next flush's stale replay then freed it again from under
    /// them, and a third customer could be handed the same live number while the second
    /// customer's order was still open. Reproduced end to end against these classes: a
    /// number issued, legitimately released, re-issued to someone else, then freed again
    /// by the stale replay — see QueueClientTest and NumberPoolTest for the scenario as
    /// a test.
    ///
    /// The fix: release by the identity of the order holding the number, not by the
    /// number's own IssuedSeq state. <c>WHERE Number = $n AND IssuedFor = $orderId</c>
    /// only matches while THIS order is still the current holder, so a stale replay for
    /// an order that no longer holds the number (because it was already released, or
    /// because the number moved on to someone else) finds no row — automatically, with
    /// no separate "already released" check needed, since a real release already clears
    /// IssuedFor along with IssuedSeq. A repeated release of the SAME still-live order is
    /// still the no-op INumberPool promises, for the same reason: the first release
    /// already cleared IssuedFor, so the repeat's WHERE no longer matches either.</summary>
    public async Task ReleaseAsync(int number, Guid orderId)
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
                    "UPDATE NumberPool SET IssuedSeq = NULL, ReleasedAtSeq = $seq, IssuedFor = NULL " +
                    "WHERE Number = $n AND IssuedFor = $orderId";
                update.Parameters.AddWithValue("$seq", seq);
                update.Parameters.AddWithValue("$n", number);
                update.Parameters.AddWithValue("$orderId", orderId.ToString());
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
    /// У третьей ветки есть обратная сторона: если выдать весь срез и
    /// затем вернуть его целиком (вырожденный случай, а не обычная смена), она
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
    /// EnsurePoolAsync всегда пишет эту строку при пересборке пула, так что
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

    /// <summary>Ключ, по которому пул считается «тем же»: день и всё, что
    /// меняет состав или порядок среза. Prefix не входит — он прибавляется к
    /// уже выданному числу. Secret не входит — он влияет только на порядок
    /// при следующей пересборке, а порядок уже лежит в таблице.</summary>
    internal static string PoolKey(string day, QueueNumberOptions o) => string.Join('|',
        day,
        o.Min.ToString(CultureInfo.InvariantCulture),
        o.Max.ToString(CultureInfo.InvariantCulture),
        o.Shuffle ? "shuffle" : "sequential",
        o.TillCount.ToString(CultureInfo.InvariantCulture),
        o.TillIndex.ToString(CultureInfo.InvariantCulture));

    /// <summary>Пересобирает пул, когда сохранённый PoolKey не совпал с
    /// вычисленным — по смене дня (местное время: граница дня — граница смены
    /// в зале, а не часовой пояс сервера) или по смене любой из настроек
    /// формы. Так настройки применяются на следующем талоне. Удаление и
    /// вставка — в одной транзакции, чтобы падение посреди пересборки не
    /// могло оставить таблицу наполовину старой, наполовину новой.
    ///
    /// Наследие: до этой настройки ключом был Day, и на действующей кассе
    /// PoolKey нет. Пересобрать просто так нельзя — порядок детерминирован по
    /// дню, IssueSeq обнулится, и касса заново раздаст утренние номера. Если
    /// Day — сегодня и настройки совпадают с константами прежнего кода
    /// (ShapesTheLegacyPool), пул на диске и есть пул от этих настроек:
    /// записать PoolKey, таблицу не трогать. Иначе — пересобрать. Day после
    /// этого не пишется и не читается; ветка отмирает на следующий день.</summary>
    private async Task EnsurePoolAsync(SqliteConnection connection, QueueNumberOptions options)
    {
        var today = _now().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var poolKey = PoolKey(today, options);

        var storedKey = await ReadStateAsync(connection, "PoolKey");
        if (storedKey == poolKey) return;

        if (storedKey == null && options.ShapesTheLegacyPool
            && await ReadStateAsync(connection, "Day") == today)
        {
            await WriteStateAsync(connection, "PoolKey", poolKey);
            return;
        }

        var slice = QueueNumberSlice.Ascending(options);
        if (options.Shuffle) Shuffle(slice, today, options);

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
                INSERT INTO QueueState (Key, Value) VALUES ('PoolKey', $PoolKey)
                    ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            ";
            state.Parameters.AddWithValue("$PoolKey", poolKey);
            await state.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task<string?> ReadStateAsync(SqliteConnection connection, string key)
    {
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT Value FROM QueueState WHERE Key = $Key";
        read.Parameters.AddWithValue("$Key", key);
        return (await read.ExecuteScalarAsync()) as string;
    }

    private static async Task WriteStateAsync(SqliteConnection connection, string key, string value)
    {
        using var write = connection.CreateCommand();
        write.CommandText = @"
            INSERT INTO QueueState (Key, Value) VALUES ($Key, $Value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
        write.Parameters.AddWithValue("$Key", key);
        write.Parameters.AddWithValue("$Value", value);
        await write.ExecuteNonQueryAsync();
    }

    /// <summary>Фишер—Йетс на потоке QueueShuffleKeystream (день, индекс
    /// кассы, общий секрет — см. его докстринг о том, почему не
    /// System.Random). Детерминированно по дню, так что перезапуск посреди
    /// дня (TheShuffleIsStableAcrossRestartsWithinADay) воспроизводит тот же
    /// порядок; на секрете, а не только на дате, чтобы порядок нельзя было
    /// предсказать по одному талону и дате на нём.</summary>
    private static void Shuffle(int[] slice, string day, QueueNumberOptions options)
    {
        var keystream = new QueueShuffleKeystream(day, options.TillIndex, options.Secret);
        for (var i = slice.Length - 1; i > 0; i--)
        {
            var j = keystream.NextIndex(i + 1);
            (slice[i], slice[j]) = (slice[j], slice[i]);
        }
    }
}
