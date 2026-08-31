using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VvCash.Models;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

/// <summary>Fixes 1+2 (post-review): CloseStaleOrdersAsync used to close every order
/// whose CreatedAt fell on a calendar day before <c>today</c> — the parameter callers
/// passed the SERVER'S clock, but the comparison itself was against the CLIENT'S
/// CreatedAt. Two shops-visible bugs fell out of that one mistake:
///
///   1. The first sale after midnight swept every order rung up the day before,
///      whether or not it was still cooking — the number went back in the pool and
///      could reach a second customer while the first was still waiting.
///   2. A till with a wrong clock (a day behind) had its OWN just-placed orders swept
///      by the very next POST /orders from ANY till, seconds after they arrived — the
///      spec's promise that clock skew between tills does not matter was true for the
///      number pools and false here.
///
/// The fix: the server now stamps its own ReceivedAt when it first stores an order
/// (QueueServer's POST /orders handler, via SaveOrderAsync's new receivedAt parameter)
/// and CloseStaleOrdersAsync judges staleness by AGE against that server-owned stamp
/// (QueueStorage.StaleOrderGracePeriod — see its docstring for why four hours), not by
/// a calendar-day cliff against the client's CreatedAt. CreatedAt keeps its old job —
/// what the kitchen screen shows the cook — but no longer decides who gets swept.
///
/// Also carries the outbox-count tests from the same original task (Task 25): those
/// are untouched by this fix and stay here rather than being shuffled to another file
/// for a rename's sake.</summary>
public class QueueDayRolloverTest
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");

    private static QueueOrder Order(
        DateTime createdAt,
        QueueOrderState state = QueueOrderState.New,
        int number = 305,
        DateTime? closedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Number = number,
        TillIndex = 0,
        State = state,
        CreatedAt = createdAt,
        ClosedAt = closedAt,
        Lines = new List<QueueOrderLine> { new() { Name = "Coffee", Quantity = "2 pcs" } }
    };

    /// <summary>Fix 1, reproduced exactly as the review described it: several orders
    /// rung up just before midnight, still cooking, and the sweep runs minutes later —
    /// "the first sale after midnight" — with a calendar day already between CreatedAt
    /// and now. Under the old CreatedAt/calendar-day rule this closed every one of them
    /// on the spot. Under the fix, ReceivedAt (server clock, set to the same moment as
    /// CreatedAt here — the till was not lying about when it took the order) is only
    /// minutes old, well inside the grace period, so none of them are touched.</summary>
    [Fact]
    public async Task TheFirstSaleAfterMidnightDoesNotSweepOrdersStillCooking()
    {
        var storage = new QueueStorage(TempDb());
        var isNew = Order(new DateTime(2026, 8, 30, 23, 58, 0), QueueOrderState.New);
        var inProgress = Order(new DateTime(2026, 8, 30, 23, 50, 0), QueueOrderState.InProgress, number: 306);
        var ready = Order(new DateTime(2026, 8, 30, 23, 40, 0), QueueOrderState.Ready, number: 307);
        await storage.SaveOrderAsync(isNew, isNew.CreatedAt);
        await storage.SaveOrderAsync(inProgress, inProgress.CreatedAt);
        await storage.SaveOrderAsync(ready, ready.CreatedAt);

        // The next calendar day, minutes later — the exact "first POST /orders after
        // midnight" scenario from the review.
        await storage.CloseStaleOrdersAsync(new DateTime(2026, 8, 31, 0, 5, 0));

        foreach (var id in new[] { isNew.Id, inProgress.Id, ready.Id })
        {
            var reloaded = await storage.GetOrderAsync(id);
            Assert.NotEqual(QueueOrderState.Closed, reloaded!.State);
            Assert.Null(reloaded.ClosedAt);
        }
    }

    /// <summary>Fix 2, reproduced exactly as the review described it: a till whose date
    /// is a day behind stamps a fresh order with a CreatedAt that LOOKS a day old, but
    /// the server received it just now. Judging by CreatedAt (the old bug) would sweep
    /// this order on the very next POST /orders from ANY till, seconds after it arrived.
    /// Judging by ReceivedAt (the fix) does not: the server's own clock says it just
    /// walked in the door, and that is what staleness is measured against.</summary>
    [Fact]
    public async Task AClientWithAClockADayBehindDoesNotGetItsFreshOrderSwept()
    {
        var storage = new QueueStorage(TempDb());
        var now = new DateTime(2026, 8, 31, 12, 0, 0);
        var brokenClockCreatedAt = now.AddDays(-1); // the till thinks it is yesterday
        var order = Order(brokenClockCreatedAt, QueueOrderState.New);

        // The server stamps ITS OWN clock, not the till's — exactly what
        // QueueServer's POST /orders handler now does before calling SaveOrderAsync.
        await storage.SaveOrderAsync(order, receivedAt: now);

        // Another till's POST /orders arrives seconds later and runs the sweep.
        await storage.CloseStaleOrdersAsync(now.AddSeconds(5));

        var reloaded = await storage.GetOrderAsync(order.Id);
        Assert.Equal(QueueOrderState.New, reloaded!.State);
        Assert.Null(reloaded.ClosedAt);
    }

    [Theory]
    [InlineData(QueueOrderState.New)]
    [InlineData(QueueOrderState.InProgress)]
    [InlineData(QueueOrderState.Ready)]
    public async Task AnOrderNoOneClosedForLongerThanTheGracePeriodIsEventuallySwept(QueueOrderState state)
    {
        var storage = new QueueStorage(TempDb());
        var now = new DateTime(2026, 8, 31, 12, 0, 0);
        var receivedAt = now - QueueStorage.StaleOrderGracePeriod - TimeSpan.FromMinutes(1);
        var order = Order(receivedAt, state); // CreatedAt = receivedAt here; only ReceivedAt matters below
        await storage.SaveOrderAsync(order, receivedAt);

        await storage.CloseStaleOrdersAsync(now);

        var reloaded = await storage.GetOrderAsync(order.Id);
        Assert.Equal(QueueOrderState.Closed, reloaded!.State);
        Assert.Equal(now, reloaded.ClosedAt);
    }

    [Fact]
    public async Task AnOrderStillWithinTheGracePeriodIsLeftAlone()
    {
        var storage = new QueueStorage(TempDb());
        var now = new DateTime(2026, 8, 31, 12, 0, 0);
        var receivedAt = now - QueueStorage.StaleOrderGracePeriod + TimeSpan.FromMinutes(1);
        var order = Order(receivedAt, QueueOrderState.InProgress);
        await storage.SaveOrderAsync(order, receivedAt);

        await storage.CloseStaleOrdersAsync(now);

        var reloaded = await storage.GetOrderAsync(order.Id);
        Assert.Equal(QueueOrderState.InProgress, reloaded!.State);
        Assert.Null(reloaded.ClosedAt);
    }

    /// <summary>Closed и Cancelled — разные исходы, и отчёт когда-нибудь
    /// спросит про разницу (см. докстринг IQueueStorage.CloseStaleOrdersAsync).
    /// Заказ, отменённый вчера, не должен молча стать «закрытым по
    /// расписанию» ни в State, ни в ClosedAt — regardless of age.</summary>
    [Fact]
    public async Task AnAlreadyCancelledOrderStaysCancelledNoMatterHowOld()
    {
        var storage = new QueueStorage(TempDb());
        var receivedAt = new DateTime(2026, 8, 20, 9, 0, 0); // days old
        var cancelledAt = new DateTime(2026, 8, 20, 12, 0, 0);
        var order = Order(receivedAt, QueueOrderState.Cancelled, closedAt: cancelledAt);
        await storage.SaveOrderAsync(order, receivedAt);

        await storage.CloseStaleOrdersAsync(new DateTime(2026, 8, 31, 9, 0, 0));

        var reloaded = await storage.GetOrderAsync(order.Id);
        Assert.Equal(QueueOrderState.Cancelled, reloaded!.State);
        Assert.Equal(cancelledAt, reloaded.ClosedAt);
    }

    /// <summary>A queue.db written before this fix (or, same shape, a row poked
    /// directly into QueueOrders in a test that bypasses SaveOrderAsync) has no
    /// ReceivedAt at all — the migration (AddColumnIfMissingAsync in QueueStorage)
    /// leaves existing rows with NULL there. CloseStaleOrdersAsync falls back to
    /// CreatedAt for exactly those rows (see its own remarks) rather than treating a
    /// missing stamp as "just arrived" (which would silently stop the sweep from ever
    /// touching pre-migration orders) or crashing on a null.</summary>
    [Fact]
    public async Task ARowWithNoReceivedAtFallsBackToCreatedAtForStaleness()
    {
        var dbPath = TempDb();
        var storage = new QueueStorage(dbPath);
        await storage.InitializeAsync();

        var now = new DateTime(2026, 8, 31, 12, 0, 0);
        var oldCreatedAt = now - QueueStorage.StaleOrderGracePeriod - TimeSpan.FromHours(1);
        var id = Guid.NewGuid();

        using (var connection = new SqliteConnection(storage.ConnectionString))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            // No ReceivedAt column value at all — same shape as a row from before the
            // migration added it.
            command.CommandText = @"
                INSERT INTO QueueOrders
                    (Id, Number, TillIndex, State, CreatedAt, ReadyAt, ClosedAt, SaleDocumentNumber, Lines)
                VALUES
                    ($Id, 309, 0, 'New', $CreatedAt, NULL, NULL, '', '[]');
            ";
            command.Parameters.AddWithValue("$Id", id.ToString());
            command.Parameters.AddWithValue("$CreatedAt", oldCreatedAt.ToString("o"));
            await command.ExecuteNonQueryAsync();
        }

        await storage.CloseStaleOrdersAsync(now);

        var reloaded = await storage.GetOrderAsync(id);
        Assert.Equal(QueueOrderState.Closed, reloaded!.State);
    }

    [Fact]
    public async Task OutboxCountReportsWhatTheBufferHolds()
    {
        var storage = new QueueStorage(TempDb());

        Assert.Equal(0, await storage.GetOutboxCountAsync("Order"));

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await storage.SaveOutboxAsync(first, "Order", "{}");
        await storage.SaveOutboxAsync(second, "Order", "{}");
        Assert.Equal(2, await storage.GetOutboxCountAsync("Order"));

        // Отклонённая строка больше не в ротации отправки — GetOutboxAsync её
        // уже не отдаёт (см. его докстринг), счётчик не должен отдавать её тоже.
        await storage.MarkOutboxRejectedAsync(first, "test");
        Assert.Equal(1, await storage.GetOutboxCountAsync("Order"));

        await storage.DeleteOutboxAsync(second);
        Assert.Equal(0, await storage.GetOutboxCountAsync("Order"));

        // Другой Kind — другой счёт: буфер общий, но заказы не должны
        // считаться в счётчике смен состояния (будущий Kind) и наоборот.
        await storage.SaveOutboxAsync(Guid.NewGuid(), "State", "{}");
        Assert.Equal(0, await storage.GetOutboxCountAsync("Order"));
        Assert.Equal(1, await storage.GetOutboxCountAsync("State"));
    }
}
