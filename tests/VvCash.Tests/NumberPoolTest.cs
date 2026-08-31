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
}
