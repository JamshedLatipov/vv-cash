using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

/// <summary>Fix 3 (post-review): GetOrdersAsync used to return every order the server
/// had ever stored — nothing filtered by age, nothing ever deleted. That single list
/// was what GET /orders answered, what the WebSocket broadcast pushed to every screen
/// on every order and every tap, and what HttpQueueTransport.GetClosedAsync polled
/// every 15 seconds from every till. Measured on a real server at 300 orders a day,
/// the closed-orders poll alone reached 750 KB after a month and 2.25 MB after six —
/// the unfiltered snapshot roughly five times that, and within a year the dominant
/// traffic on the shop's network.
///
/// The fix splits the one unbounded method into two scoped ones — GetLiveOrdersAsync
/// (screens: a finished order is not on the board) and GetRecentlyClosedOrdersAsync
/// (the client's number-release poll: see QueueStorage.RecentlyClosedWindow for why the
/// window is generous rather than tight) — plus PurgeOldClosedOrdersAsync, which
/// actually deletes what neither of those two hands out any more (see
/// QueueStorage.ClosedOrderRetention). GetOrderAsync (single order by id) is
/// deliberately untouched: the state-transition endpoint needs to find ANY order,
/// including an already-closed one, to answer a repeat tap with 409 rather than a
/// silent 404.</summary>
public class QueueOrderRetentionTest
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");

    private static QueueOrder Order(
        QueueOrderState state, int number, DateTime createdAt, DateTime? closedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Number = number,
        TillIndex = 0,
        State = state,
        CreatedAt = createdAt,
        ClosedAt = closedAt,
        Lines = new List<QueueOrderLine> { new() { Name = "Coffee", Quantity = "1 pc" } }
    };

    private static readonly DateTime Day = new(2026, 8, 31, 12, 0, 0);

    [Fact]
    public async Task LiveOrdersExcludeClosedAndCancelled()
    {
        var storage = new QueueStorage(TempDb());
        var live1 = Order(QueueOrderState.New, 301, Day);
        var live2 = Order(QueueOrderState.InProgress, 302, Day);
        var live3 = Order(QueueOrderState.Ready, 303, Day);
        var closed = Order(QueueOrderState.Closed, 304, Day, closedAt: Day);
        var cancelled = Order(QueueOrderState.Cancelled, 305, Day, closedAt: Day);
        foreach (var o in new[] { live1, live2, live3, closed, cancelled })
            await storage.SaveOrderAsync(o, o.CreatedAt);

        var result = await storage.GetLiveOrdersAsync();

        Assert.Equal(3, result.Count);
        Assert.Contains(result, o => o.Id == live1.Id);
        Assert.Contains(result, o => o.Id == live2.Id);
        Assert.Contains(result, o => o.Id == live3.Id);
        Assert.DoesNotContain(result, o => o.Id == closed.Id);
        Assert.DoesNotContain(result, o => o.Id == cancelled.Id);
    }

    [Fact]
    public async Task RecentlyClosedOrdersOnlyIncludesClosedAndCancelled()
    {
        var storage = new QueueStorage(TempDb());
        var live = Order(QueueOrderState.New, 301, Day);
        var closed = Order(QueueOrderState.Closed, 302, Day, closedAt: Day);
        var cancelled = Order(QueueOrderState.Cancelled, 303, Day, closedAt: Day);
        foreach (var o in new[] { live, closed, cancelled })
            await storage.SaveOrderAsync(o, o.CreatedAt);

        var result = await storage.GetRecentlyClosedOrdersAsync(Day);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, o => o.Id == closed.Id);
        Assert.Contains(result, o => o.Id == cancelled.Id);
        Assert.DoesNotContain(result, o => o.Id == live.Id);
    }

    /// <summary>The window this guards is what makes a till that missed several hours
    /// of polling (a long network outage — see QueueStorage.RecentlyClosedWindow's own
    /// remarks on why 24h) still see its own closed orders and return their numbers to
    /// the pool, instead of losing that number for the rest of the day.</summary>
    [Fact]
    public async Task AnOrderClosedWithinTheWindowIsReturnedAnOrderClosedBeforeItIsNot()
    {
        var storage = new QueueStorage(TempDb());
        var now = Day;
        var withinWindow = Order(QueueOrderState.Closed, 301, Day.AddHours(-1),
            closedAt: now - QueueStorage.RecentlyClosedWindow + TimeSpan.FromMinutes(1));
        var beforeWindow = Order(QueueOrderState.Closed, 302, Day.AddHours(-2),
            closedAt: now - QueueStorage.RecentlyClosedWindow - TimeSpan.FromMinutes(1));
        await storage.SaveOrderAsync(withinWindow, withinWindow.CreatedAt);
        await storage.SaveOrderAsync(beforeWindow, beforeWindow.CreatedAt);

        var result = await storage.GetRecentlyClosedOrdersAsync(now);

        Assert.Contains(result, o => o.Id == withinWindow.Id);
        Assert.DoesNotContain(result, o => o.Id == beforeWindow.Id);
    }

    [Fact]
    public async Task PurgeDeletesClosedOrdersOlderThanRetentionAndLeavesRecentOnesAlone()
    {
        var storage = new QueueStorage(TempDb());
        var now = Day;
        var old = Order(QueueOrderState.Closed, 301, Day.AddDays(-10),
            closedAt: now - QueueStorage.ClosedOrderRetention - TimeSpan.FromMinutes(1));
        var recent = Order(QueueOrderState.Closed, 302, Day.AddHours(-1),
            closedAt: now - QueueStorage.ClosedOrderRetention + TimeSpan.FromMinutes(1));
        await storage.SaveOrderAsync(old, old.CreatedAt);
        await storage.SaveOrderAsync(recent, recent.CreatedAt);

        await storage.PurgeOldClosedOrdersAsync(now);

        Assert.Null(await storage.GetOrderAsync(old.Id));
        Assert.NotNull(await storage.GetOrderAsync(recent.Id));
    }

    /// <summary>Purge only ever looks at Closed/Cancelled — a live order that has simply
    /// been sitting around (still New, say, on a till with no kitchen screen closing
    /// anything — see the design doc's own degenerate case) must never be deleted by
    /// this sweep. CloseStaleOrdersAsync is the one that decides when a live order
    /// stops being live; purge only cleans up after that decision, never makes it.</summary>
    [Fact]
    public async Task PurgeNeverTouchesLiveOrdersRegardlessOfAge()
    {
        var storage = new QueueStorage(TempDb());
        var now = Day;
        var veryOldButStillLive = Order(QueueOrderState.New, 301, Day.AddDays(-30));
        await storage.SaveOrderAsync(veryOldButStillLive, veryOldButStillLive.CreatedAt);

        await storage.PurgeOldClosedOrdersAsync(now);

        Assert.NotNull(await storage.GetOrderAsync(veryOldButStillLive.Id));
    }

    [Fact]
    public async Task GetOrderAsyncStillFindsAnOrderRegardlessOfStateOrAge()
    {
        var storage = new QueueStorage(TempDb());
        var old = Order(QueueOrderState.Closed, 301, Day.AddDays(-30), closedAt: Day.AddDays(-30));
        await storage.SaveOrderAsync(old, old.CreatedAt);

        // Not purged yet — GetOrderAsync (single lookup) must still find it even though
        // neither GetLiveOrdersAsync nor GetRecentlyClosedOrdersAsync(Day) would.
        Assert.NotNull(await storage.GetOrderAsync(old.Id));
        Assert.DoesNotContain(await storage.GetLiveOrdersAsync(), o => o.Id == old.Id);
        Assert.DoesNotContain(await storage.GetRecentlyClosedOrdersAsync(Day), o => o.Id == old.Id);
    }
}
