using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

/// <summary>Пул номеров. Главное требование заказчика — по двум талонам нельзя
/// посчитать, сколько чеков пробито за день, поэтому «не подряд» здесь такое же
/// требование, как «без дубликатов».</summary>
public class NumberPoolTest
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");

    private static NumberPool Pool(int tillIndex = 0, string? db = null, Func<DateTime>? now = null)
        => Pool(() => QueueNumberOptions.Default(tillIndex, "secret"), db, now);

    private static NumberPool Pool(Func<QueueNumberOptions> options, string? db = null, Func<DateTime>? now = null)
        => new(new QueueStorage(db ?? TempDb()), options, now ?? (() => new DateTime(2026, 8, 31, 10, 0, 0)));

    /// <summary>None of the tests in this file release a number for a different order
    /// than the one that issued it, so a fresh Guid per call is all any of them need —
    /// the release-by-identity tests below track their own ids explicitly instead.</summary>
    private static Task<int> Issue(NumberPool pool) => pool.IssueAsync(Guid.NewGuid());

    /// <summary>Reads NumberPool.IssuedSeq for <paramref name="number"/> straight off
    /// disk, bypassing IssueAsync's own selection logic entirely. Used by
    /// AStaleReleaseForAReissuedNumberDoesNotFreeALiveOrder below because that test's
    /// property of interest — "is the number still marked issued right now" — is a fact
    /// about one row. It could be inferred from the next IssueAsync call instead: with
    /// the slice exhausted and nothing else freed, a broken guard would leave the number
    /// free and SelectNumberToIssueAsync's freed-first branch would hand it straight
    /// back out. But that inference rides on the branch ordering — it is exactly what a
    /// future reordering of the branches would silently break — whereas the row is what
    /// the guard actually protects, so the row is what the test asserts on.</summary>
    private static async Task<bool> IsStillIssuedAsync(string db, int number)
    {
        using var connection = new SqliteConnection($"Data Source={db}");
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT IssuedSeq FROM NumberPool WHERE Number = $n";
        cmd.Parameters.AddWithValue("$n", number);
        var result = await cmd.ExecuteScalarAsync();
        return result != null && result != DBNull.Value;
    }

    [Fact]
    public async Task IssuedNumbersAreThreeDigitsAndBelongToThisTillsSlice()
    {
        var pool = Pool(tillIndex: 2);

        for (var i = 0; i < 20; i++)
        {
            var number = await Issue(pool);
            Assert.InRange(number, 100, 999);
            Assert.Equal(2, number % 5);
        }
    }

    [Fact]
    public async Task TwoTillsNeverCollide()
    {
        var first = Pool(tillIndex: 0);
        var second = Pool(tillIndex: 1);

        var a = new List<int>();
        var b = new List<int>();
        for (var i = 0; i < 50; i++)
        {
            a.Add(await Issue(first));
            b.Add(await Issue(second));
        }

        Assert.Empty(a.Intersect(b));
    }

    [Fact]
    public async Task NoNumberIsIssuedTwiceWhileTheSliceLasts()
    {
        var pool = Pool();

        var issued = new List<int>();
        for (var i = 0; i < 180; i++) issued.Add(await Issue(pool));

        Assert.Equal(180, issued.Distinct().Count());
    }

    /// <summary>Тот самый анти-подсчёт. Порог мягкий нарочно: доказывать
    /// случайность одним прогоном нельзя, а поймать «забыли перемешать» — можно,
    /// и это ровно та ошибка, которая проходит все прочие тесты.</summary>
    [Fact]
    public async Task IssueOrderIsNotMonotonic()
    {
        var pool = Pool();

        var issued = new List<int>();
        for (var i = 0; i < 30; i++) issued.Add(await Issue(pool));

        var ascendingSteps = issued.Zip(issued.Skip(1), (a, b) => b > a).Count(x => x);
        Assert.InRange(ascendingSteps, 5, 24);
    }

    [Fact]
    public async Task TheShuffleIsStableAcrossRestartsWithinADay()
    {
        var db = TempDb();
        var first = Pool(db: db);
        var a = await Issue(first);
        var b = await Issue(first);

        var reopened = Pool(db: db);
        var c = await Issue(reopened);

        Assert.NotEqual(a, c);
        Assert.NotEqual(b, c);
    }

    /// <summary>Свободный номер возвращается в оборот только после всех свежих
    /// и всех освобождённых раньше него — это то, что заменило кулдаун (см.
    /// SelectNumberToIssueAsync). Из 180 выдано 100, освобождён первый; пока
    /// свежие не кончились, по ходу освобождаются ещё два — A, потом B. Все 80
    /// оставшихся выдач — свежие; 181-я — первый, затем A, затем B: в порядке
    /// возврата, а не в порядке номеров и не в порядке выдачи.
    ///
    /// Возврат сам не двигает seq, поэтому между освобождениями стоит по
    /// выдаче — иначе A и B получили бы один ReleasedAtSeq, и «кто раньше»
    /// решал бы не порядок возврата, а меньший Number (он же rowid).</summary>
    [Fact]
    public async Task AFreedNumberReturnsAfterEveryFreshAndEarlierFreedNumber()
    {
        var pool = Pool();

        var issued = new List<int>();
        var issuedIds = new List<Guid>();
        for (var i = 0; i < 100; i++)
        {
            var id = Guid.NewGuid();
            issuedIds.Add(id);
            issued.Add(await pool.IssueAsync(id));
        }

        var first = issued[0];
        var later = issued[50];   // возвращён вторым — но номер выдан позже, чем earlier
        var earlier = issued[10]; // возвращён третьим — но номер выдан раньше, чем later
        var freed = new[] { first, later, earlier };

        await pool.ReleaseAsync(first, issuedIds[0]);          // ReleasedAtSeq 100

        for (var i = 0; i < 40; i++)
            Assert.DoesNotContain(await Issue(pool), freed);   // свежие ещё есть, seq -> 140

        await pool.ReleaseAsync(later, issuedIds[50]);         // ReleasedAtSeq 140

        Assert.DoesNotContain(await Issue(pool), freed);       // свежий, seq -> 141

        await pool.ReleaseAsync(earlier, issuedIds[10]);       // ReleasedAtSeq 141

        for (var i = 0; i < 39; i++)
            Assert.DoesNotContain(await Issue(pool), freed);   // последние свежие, seq -> 180

        // Свежих больше нет — свободные по моменту возврата.
        Assert.Equal(first, await Issue(pool));
        Assert.Equal(later, await Issue(pool));
        Assert.Equal(earlier, await Issue(pool));
    }

    [Fact]
    public async Task AnExhaustedSliceReusesTheOldestRatherThanStalling()
    {
        var pool = Pool();

        for (var i = 0; i < 180; i++) await Issue(pool);
        var afterExhaustion = await Issue(pool);

        Assert.InRange(afterExhaustion, 100, 999);
        Assert.Equal(0, afterExhaustion % 5);
    }

    /// <summary>A closed order is reported by the server on every poll until
    /// something deletes it, and nothing does — QueueFlushLoop calls
    /// FlushAsync every 15 seconds, so the same closed order gets released
    /// again and again, not once. A repeat release must be a no-op: it must
    /// not re-stamp ReleasedAtSeq and thereby move the number BACK in the
    /// freed queue, behind numbers that were freed after it.
    ///
    /// Under the release-by-identity guard this falls out for free: the first
    /// release already clears IssuedFor, so the repeat (same orderId) matches
    /// no row and is a no-op — it does not need a separate "already released"
    /// check. This test is the observable consequence of that no-op: A freed
    /// at seq 178, B at seq 179, A's release replayed at seq 180 — A still
    /// comes out before B. Had the replay re-stamped A to 180, B would have
    /// come out first.</summary>
    [Fact]
    public async Task ARepeatedReleaseDoesNotMoveTheNumberBackInTheFreedQueue()
    {
        var pool = Pool();

        // Issue all but two so the last two fresh issues can sit between the
        // releases and give A and B distinct ReleasedAtSeq (a release itself
        // does not move seq).
        var issued = new List<int>();
        var issuedIds = new List<Guid>();
        for (var i = 0; i < 178; i++)
        {
            var id = Guid.NewGuid();
            issuedIds.Add(id);
            issued.Add(await pool.IssueAsync(id));
        }

        var a = issued[0];
        var aId = issuedIds[0];
        var b = issued[1];
        var bId = issuedIds[1];

        await pool.ReleaseAsync(a, aId);                       // A freed @178
        Assert.DoesNotContain(await Issue(pool), new[] { a, b }); // fresh #179
        await pool.ReleaseAsync(b, bId);                       // B freed @179
        Assert.DoesNotContain(await Issue(pool), new[] { a, b }); // fresh #180, the last one

        // The stale repeat: A's order reported closed again, at seq 180.
        await pool.ReleaseAsync(a, aId);

        Assert.Equal(a, await Issue(pool));
        Assert.Equal(b, await Issue(pool));
    }

    /// <summary>The reviewer's reproduction, against the real NumberPool. The old guard
    /// was `WHERE Number = $n AND IssuedSeq IS NOT NULL` — meant to make a repeated
    /// release a no-op, on the claim that a stale release for a re-issued number "simply
    /// finds no row". Wrong: a re-issued number still has IssuedSeq IS NOT NULL (issued,
    /// just to somebody else), so the stale release matched it and freed a live number
    /// out from under its actual, current holder.
    ///
    /// This test issues a number to a first order, releases it for real, and — with
    /// the slice exhausted and nothing else freed — the very next issue hands the SAME
    /// number to a second order (freed numbers go out before live ones are stolen, see
    /// SelectNumberToIssueAsync). It then replays the FIRST order's release again —
    /// exactly what QueueClient.FlushAsync does every 15 seconds, forever, because
    /// QueueServer's GET /orders returns every order ever stored and nothing ever
    /// deletes a closed one. Against the old guard the final assert below fails: the
    /// stale replay frees the number out from under the second order while it is still
    /// open, and the next IssueAsync would hand it to a third customer. Against the new
    /// guard (release by orderId, not by IssuedSeq state) the replay matches no row,
    /// because IssuedFor now names the second order, not the first.</summary>
    [Fact]
    public async Task AStaleReleaseForAReissuedNumberDoesNotFreeALiveOrder()
    {
        var db = TempDb();
        var pool = Pool(db: db);

        // Exhaust the slice first: with fresh numbers still available the pool would
        // just hand one of those out instead of the one this test cares about, and
        // the scenario would not fire.
        var issued = new List<int>();
        var issuedIds = new List<Guid>();
        for (var i = 0; i < 180; i++)
        {
            var id = Guid.NewGuid();
            issuedIds.Add(id);
            issued.Add(await pool.IssueAsync(id));
        }

        var target = issued[0];
        var firstHolder = issuedIds[0];

        // The first holder's order closes for real.
        await pool.ReleaseAsync(target, firstHolder);

        // No fresh numbers and nothing else freed: the next issue is `target`.
        var secondHolder = Guid.NewGuid();
        var reissued = await pool.IssueAsync(secondHolder);
        Assert.Equal(target, reissued); // sanity: this really is the same ticket number
        Assert.True(await IsStillIssuedAsync(db, target)); // sanity: it is live right now

        // The server never deletes closed orders, so the stale "closed" for the FIRST
        // holder's (already-closed) order is replayed on the next 15-second flush.
        await pool.ReleaseAsync(target, firstHolder);

        // The number must still belong to the second holder — the stale replay for an
        // order that no longer holds it must be the no-op INumberPool promises, not a
        // release of whoever holds it now. Checked directly on the row rather than by
        // issuing another number: see IsStillIssuedAsync's own remarks on why the row,
        // not the branch ordering, is the thing to assert on.
        Assert.True(await IsStillIssuedAsync(db, target));
    }

    [Fact]
    public async Task ANewDayResetsAndReshuffles()
    {
        var db = TempDb();
        var day = new DateTime(2026, 8, 31, 10, 0, 0);
        var pool = Pool(db: db, now: () => day);

        var yesterday = new List<int>();
        for (var i = 0; i < 180; i++) yesterday.Add(await Issue(pool));

        day = day.AddDays(1);
        var today = new List<int>();
        for (var i = 0; i < 180; i++) today.Add(await Issue(pool));

        // Сброс: весь срез снова свободен. Без сброса пул донашивал бы
        // третью ветку и повторил бы вчерашний порядок один в один.
        Assert.Equal(180, today.Distinct().Count());
        Assert.Equal(yesterday.OrderBy(n => n), today.OrderBy(n => n));
        // Перешаффл: те же номера, но не на тех же местах.
        var samePosition = yesterday.Zip(today, (a, b) => a == b).Count(x => x);
        Assert.InRange(samePosition, 0, 10);
    }

    /// <summary>Читает строку QueueState напрямую — PoolKey и Day не входят в
    /// публичный контракт пула, но именно они и есть предмет тестов усыновления
    /// ниже.</summary>
    private static async Task<string?> StateAsync(string db, string key)
    {
        using var connection = new SqliteConnection($"Data Source={db}");
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Value FROM QueueState WHERE Key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return await cmd.ExecuteScalarAsync() as string;
    }

    [Fact]
    public async Task ASequentialPoolIssuesItsBlockInOrder()
    {
        var pool = Pool(() => new QueueNumberOptions(1, 2, 1, 99, false, "", "secret"));

        var issued = new List<int>();
        for (var i = 0; i < 49; i++) issued.Add(await Issue(pool));

        Assert.Equal(Enumerable.Range(51, 49).ToArray(), issued);
    }

    [Fact]
    public async Task ASingleTillDrawsFromTheWholeRange()
    {
        var pool = Pool(() => new QueueNumberOptions(0, 1, 1, 30, true, "", "secret"));

        var issued = new List<int>();
        for (var i = 0; i < 30; i++) issued.Add(await Issue(pool));

        Assert.Equal(Enumerable.Range(1, 30), issued.OrderBy(n => n));
    }

    [Fact]
    public async Task ChangingTheRangeMidDayRebuildsThePoolOnTheNextIssue()
    {
        var db = TempDb();
        var options = QueueNumberOptions.Default(0, "secret");
        var pool = Pool(() => options, db);

        var before = await Issue(pool);
        Assert.InRange(before, 100, 999);

        options = options with { Min = 1, Max = 30 };
        var after = await Issue(pool);

        Assert.InRange(after, 1, 30);
    }

    [Fact]
    public async Task TheSameOptionsAgainDoNotRebuildThePool()
    {
        var db = TempDb();
        var pool = Pool(() => QueueNumberOptions.Default(0, "secret"), db);

        var first = await Issue(pool);
        var second = await Issue(pool);
        // Новый экземпляр над тем же файлом — перезапуск кассы.
        var restarted = Pool(() => QueueNumberOptions.Default(0, "secret"), db);
        var third = await Issue(restarted);

        Assert.Equal(3, new[] { first, second, third }.Distinct().Count());
    }

    [Fact]
    public async Task ChangingTheTillIndexMidDayRebuildsThePool()
    {
        var db = TempDb();
        var options = QueueNumberOptions.Default(0, "secret");
        var pool = Pool(() => options, db);

        Assert.Equal(0, await Issue(pool) % 5);

        options = options with { TillIndex = 3 };

        Assert.Equal(3, await Issue(pool) % 5);
    }

    /// <summary>База, которую этот код застаёт на действующей кассе: ключ Day,
    /// ключа PoolKey нет, три номера уже роздано с утра. Пересобрать нельзя —
    /// порядок детерминирован по дню, IssueSeq обнулится, и те же три номера
    /// уйдут второму клиенту. Пул усыновляется: продолжает с четвёртого.</summary>
    [Fact]
    public async Task ALegacyPoolFromTodayIsAdoptedNotRebuilt()
    {
        var db = TempDb();
        var day = new DateTime(2026, 9, 22, 10, 0, 0);
        var legacy = Pool(() => QueueNumberOptions.Default(0, "secret"), db, () => day);

        var morning = new List<int>();
        for (var i = 0; i < 3; i++) morning.Add(await Issue(legacy));

        // Превращаем базу в «старую»: PoolKey стираем, Day пишем, как писал прежний код.
        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM QueueState WHERE Key = 'PoolKey';
                INSERT INTO QueueState (Key, Value) VALUES ('Day', '2026-09-22')
                    ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
            await cmd.ExecuteNonQueryAsync();
        }

        var updated = Pool(() => QueueNumberOptions.Default(0, "secret"), db, () => day);
        var fourth = await Issue(updated);

        Assert.DoesNotContain(fourth, morning);
        Assert.NotNull(await StateAsync(db, "PoolKey"));
        Assert.Equal("2026-09-22", await StateAsync(db, "Day"));
    }

    [Fact]
    public async Task ALegacyPoolIsRebuiltWhenTheSettingsAlreadyDiffer()
    {
        var db = TempDb();
        var day = new DateTime(2026, 9, 22, 10, 0, 0);
        var legacy = Pool(() => QueueNumberOptions.Default(0, "secret"), db, () => day);
        await Issue(legacy);

        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM QueueState WHERE Key = 'PoolKey';
                INSERT INTO QueueState (Key, Value) VALUES ('Day', '2026-09-22')
                    ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
            await cmd.ExecuteNonQueryAsync();
        }

        var narrowed = Pool(() => QueueNumberOptions.Default(0, "secret") with { Max = 500 }, db, () => day);

        Assert.InRange(await Issue(narrowed), 100, 500);
        // Диапазон не различает исходы: второй номер старого пула — 250, он и так
        // в 100–500. Различает IssueSeq: пересборка обнуляет его и выдача даёт 1,
        // а ошибочное усыновление оставило бы 2.
        Assert.Equal("1", await StateAsync(db, "IssueSeq"));
    }

    /// <summary>Одна касса на точке — срез в 900 номеров, длиннее 256, на
    /// которые был рассчитан однобайтовый бросок QueueShuffleKeystream. До
    /// двухбайтовой ветки перемешивание бросало на каждой выдаче, и касса
    /// молча оставалась без номеров.</summary>
    [Fact]
    public async Task ASingleTillShufflesTheWholeDefaultRange()
    {
        var pool = Pool(() => QueueNumberOptions.Default(0, "secret") with { TillCount = 1 });

        var issued = new List<int>();
        for (var i = 0; i < 900; i++) issued.Add(await Issue(pool));

        Assert.Equal(Enumerable.Range(100, 900), issued.OrderBy(n => n));
        var first20 = issued.Take(20).ToList();
        var ascendingSteps = first20.Zip(first20.Skip(1), (a, b) => b > a).Count(x => x);
        Assert.InRange(ascendingSteps, 1, 18);
    }

    [Fact]
    public async Task AFourDigitRangeShufflesWithoutThrowing()
    {
        var pool = Pool(() => new QueueNumberOptions(0, 1, 1, 9999, true, "", "secret"));

        for (var i = 0; i < 5; i++)
            Assert.InRange(await Issue(pool), 1, 9999);
    }

    [Fact]
    public async Task AnEmptySliceThrowsRatherThanIssuingSomeoneElsesNumber()
    {
        var pool = Pool(() => new QueueNumberOptions(4, 5, 1, 3, true, "", "secret"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Issue(pool));
    }

    /// <summary>Срез из трёх номеров, все выданы, второй вернули. Свободный
    /// второй обязан выйти раньше живого первого, который клиент ещё держит.
    /// Прежний ORDER BY COALESCE(IssuedSeq, ReleasedAtSeq) выбирал первый:
    /// у него seq 1, у возвращённого — 3.</summary>
    [Fact]
    public async Task ExhaustionPrefersAFreedNumberOverAliveOne()
    {
        var pool = Pool(() => new QueueNumberOptions(0, 1, 1, 3, false, "", "secret"));

        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var issued = new List<int>();
        foreach (var id in ids) issued.Add(await pool.IssueAsync(id));
        Assert.Equal(new[] { 1, 2, 3 }, issued);

        await pool.ReleaseAsync(2, ids[1]);

        Assert.Equal(2, await Issue(pool));
    }

    [Fact]
    public async Task ExhaustionWithNothingFreedTakesTheOldestLiveNumber()
    {
        var pool = Pool(() => new QueueNumberOptions(0, 1, 1, 3, false, "", "secret"));

        for (var i = 0; i < 3; i++) await Issue(pool);

        Assert.Equal(1, await Issue(pool));
    }
}
