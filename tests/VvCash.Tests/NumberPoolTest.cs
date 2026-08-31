using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

    [Fact]
    public async Task IssuedNumbersAreThreeDigitsAndBelongToThisTillsSlice()
    {
        var pool = Pool(tillIndex: 2);

        for (var i = 0; i < 20; i++)
        {
            var number = await pool.IssueAsync();
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
            a.Add(await first.IssueAsync());
            b.Add(await second.IssueAsync());
        }

        Assert.Empty(a.Intersect(b));
    }

    [Fact]
    public async Task NoNumberIsIssuedTwiceWhileTheSliceLasts()
    {
        var pool = Pool();

        var issued = new List<int>();
        for (var i = 0; i < 180; i++) issued.Add(await pool.IssueAsync());

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
        for (var i = 0; i < 30; i++) issued.Add(await pool.IssueAsync());

        var ascendingSteps = issued.Zip(issued.Skip(1), (a, b) => b > a).Count(x => x);
        Assert.InRange(ascendingSteps, 5, 24);
    }

    [Fact]
    public async Task TheShuffleIsStableAcrossRestartsWithinADay()
    {
        var db = TempDb();
        var first = Pool(db: db);
        var a = await first.IssueAsync();
        var b = await first.IssueAsync();

        var reopened = Pool(db: db);
        var c = await reopened.IssueAsync();

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
        for (var i = 0; i < 180; i++) issued.Add(await pool.IssueAsync());

        var released = issued[0];
        await pool.ReleaseAsync(released);

        for (var i = 0; i < NumberPool.CooldownIssues - 1; i++)
            Assert.NotEqual(released, await pool.IssueAsync());

        Assert.Equal(released, await pool.IssueAsync());
    }

    [Fact]
    public async Task AnExhaustedSliceReusesTheOldestRatherThanStalling()
    {
        var pool = Pool();

        for (var i = 0; i < 180; i++) await pool.IssueAsync();
        var afterExhaustion = await pool.IssueAsync();

        Assert.InRange(afterExhaustion, 100, 999);
        Assert.Equal(0, afterExhaustion % 5);
    }

    /// <summary>A closed order is reported by the server on every poll until
    /// something deletes it, and nothing does — QueueFlushLoop calls
    /// FlushAsync every 15 seconds, so the same closed order gets released
    /// again and again, not once. A repeat release must be a no-op: it must
    /// not push the cooldown window out from under the first, real release.
    /// Anchor stays at the original release's seq (180); the window opens at
    /// seq 230, not at seq 240 as it would if the repeat re-stamped it.</summary>
    [Fact]
    public async Task ARepeatedReleaseDoesNotPushTheCooldownWindowOut()
    {
        var pool = Pool();

        // Exhaust so branch 1 (fresh) is empty and the cooldown branch is
        // what actually decides, same setup as the cooldown-width test above.
        var issued = new List<int>();
        for (var i = 0; i < 180; i++) issued.Add(await pool.IssueAsync());

        var released = issued[0];
        await pool.ReleaseAsync(released); // real release, anchored at seq 180

        for (var i = 0; i < 10; i++) await pool.IssueAsync(); // seq -> 190

        // The stale repeat: same number, reported closed again.
        await pool.ReleaseAsync(released);

        for (var i = 0; i < NumberPool.CooldownIssues - 10 - 1; i++) // seq 191..229
            Assert.NotEqual(released, await pool.IssueAsync());

        Assert.Equal(released, await pool.IssueAsync()); // seq 230
    }

    [Fact]
    public async Task ANewDayResetsAndReshuffles()
    {
        var db = TempDb();
        var day = new DateTime(2026, 8, 31, 10, 0, 0);
        var pool = Pool(db: db, now: () => day);

        var yesterday = new List<int>();
        for (var i = 0; i < 180; i++) yesterday.Add(await pool.IssueAsync());

        day = day.AddDays(1);
        var today = new List<int>();
        for (var i = 0; i < 180; i++) today.Add(await pool.IssueAsync());

        // Сброс: весь срез снова свободен. Без сброса пул донашивал бы
        // третью ветку и повторил бы вчерашний порядок один в один.
        Assert.Equal(180, today.Distinct().Count());
        Assert.Equal(yesterday.OrderBy(n => n), today.OrderBy(n => n));
        // Перешаффл: те же номера, но не на тех же местах.
        var samePosition = yesterday.Zip(today, (a, b) => a == b).Count(x => x);
        Assert.InRange(samePosition, 0, 10);
    }
}
