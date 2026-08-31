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
        => new(new QueueStorage(db ?? TempDb()), tillIndex, "secret", now ?? (() => new DateTime(2026, 8, 31, 10, 0, 0)));

    /// <summary>None of the tests in this file release a number for a different order
    /// than the one that issued it, so a fresh Guid per call is all any of them need —
    /// the release-by-identity tests below track their own ids explicitly instead.</summary>
    private static Task<int> Issue(NumberPool pool) => pool.IssueAsync(Guid.NewGuid());

    /// <summary>Reads NumberPool.IssuedSeq for <paramref name="number"/> straight off
    /// disk, bypassing IssueAsync's own selection logic entirely. Used by
    /// AStaleReleaseForAReissuedNumberDoesNotFreeALiveOrder below because that test's
    /// property of interest — "is the number still marked issued right now" — is a fact
    /// about one row, not something safe to infer from a few more IssueAsync calls: with
    /// the slice fully exhausted, SelectNumberToIssueAsync's third (oldest-first) branch
    /// can legitimately keep cycling through the ~130 other exhausted numbers with a
    /// lower COALESCE(IssuedSeq, ReleasedAtSeq, 0) than the one under test for a long
    /// stretch regardless of whether the guard bug fired, so "the next N issues never
    /// return it" would not actually distinguish the fixed guard from the broken one.</summary>
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

    /// <summary>Возврат сам не двигает seq — все освобождённые номера получают
    /// один и тот же ReleasedAtSeq, и окно кулдауна открывается ровно через
    /// CooldownIssues выдач после него, не раньше и не позже. Цикл ловит
    /// слишком короткий кулдаун на первой же итерации; финальный assert —
    /// слишком длинный.</summary>
    [Fact]
    public async Task TheCooldownIsExactlyFiftyIssuesWide()
    {
        var pool = Pool();

        // Исчерпываем срез, чтобы первая ветка была пуста и решение реально
        // принимал кулдаун, а не он же в обход, потому что до него не дошло.
        var issued = new List<int>();
        var issuedIds = new List<Guid>();
        for (var i = 0; i < 180; i++)
        {
            var id = Guid.NewGuid();
            issuedIds.Add(id);
            issued.Add(await pool.IssueAsync(id));
        }

        var released = issued[0];
        await pool.ReleaseAsync(released, issuedIds[0]);

        for (var i = 0; i < NumberPool.CooldownIssues - 1; i++)
            Assert.NotEqual(released, await Issue(pool));

        Assert.Equal(released, await Issue(pool));
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
    /// not push the cooldown window out from under the first, real release.
    /// Anchor stays at the original release's seq (180); the window opens at
    /// seq 230, not at seq 240 as it would if the repeat re-stamped it.
    ///
    /// Under the release-by-identity guard this falls out for free: the first
    /// release already clears IssuedFor, so the repeat (same orderId) matches
    /// no row and is a no-op — it does not need a separate "already released"
    /// check.</summary>
    [Fact]
    public async Task ARepeatedReleaseDoesNotPushTheCooldownWindowOut()
    {
        var pool = Pool();

        // Exhaust so branch 1 (fresh) is empty and the cooldown branch is
        // what actually decides, same setup as the cooldown-width test above.
        var issued = new List<int>();
        var issuedIds = new List<Guid>();
        for (var i = 0; i < 180; i++)
        {
            var id = Guid.NewGuid();
            issuedIds.Add(id);
            issued.Add(await pool.IssueAsync(id));
        }

        var released = issued[0];
        var releasedId = issuedIds[0];
        await pool.ReleaseAsync(released, releasedId); // real release, anchored at seq 180

        for (var i = 0; i < 10; i++) await Issue(pool); // seq -> 190

        // The stale repeat: same order, reported closed again.
        await pool.ReleaseAsync(released, releasedId);

        for (var i = 0; i < NumberPool.CooldownIssues - 10 - 1; i++) // seq 191..229
            Assert.NotEqual(released, await Issue(pool));

        Assert.Equal(released, await Issue(pool)); // seq 230
    }

    /// <summary>The reviewer's reproduction, against the real NumberPool. The old guard
    /// was `WHERE Number = $n AND IssuedSeq IS NOT NULL` — meant to make a repeated
    /// release a no-op, on the claim that a stale release for a re-issued number "simply
    /// finds no row". Wrong: a re-issued number still has IssuedSeq IS NOT NULL (issued,
    /// just to somebody else), so the stale release matched it and freed a live number
    /// out from under its actual, current holder.
    ///
    /// This test issues a number to a first order, releases it for real, lets the
    /// cooldown pass so the SAME number is re-issued to a second order, then replays the
    /// FIRST order's release again — exactly what QueueClient.FlushAsync does every 15
    /// seconds, forever, because QueueServer's GET /orders returns every order ever
    /// stored and nothing ever deletes a closed one. Against the old guard the final loop
    /// below fails: the stale replay frees the number early and a third IssueAsync call
    /// hands it right back out while the second order is still open. Against the new
    /// guard (release by orderId, not by IssuedSeq state) the replay matches no row,
    /// because IssuedFor now names the second order, not the first.</summary>
    [Fact]
    public async Task AStaleReleaseForAReissuedNumberDoesNotFreeALiveOrder()
    {
        var db = TempDb();
        var pool = Pool(db: db);

        // Exhaust the slice first (same reasoning as the cooldown tests above): with
        // fresh numbers still available the pool would just hand one of those out
        // instead of the one this test cares about, and the scenario would not fire.
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

        // 49 further sales elsewhere; the CooldownIssues-th issue after the release is
        // the one that returns `target` (see TheCooldownIsExactlyFiftyIssuesWide above).
        for (var i = 0; i < NumberPool.CooldownIssues - 1; i++) await Issue(pool);

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
        // issuing more numbers: see IsStillIssuedAsync's own remarks on why a few more
        // IssueAsync calls would not reliably surface this, even against the broken
        // guard, once the slice is this exhausted.
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
}
