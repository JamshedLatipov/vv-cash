using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

/// <summary>Fail-open: сервер лежит — номер всё равно выдан, бумага всё равно
/// вышла, заказ лёг в буфер. Продажа не встаёт никогда, это решение спеки.</summary>
public class QueueClientTest
{
    private sealed class FakeTransport : IQueueTransport
    {
        public bool Reachable { get; set; } = true;

        /// <summary>Id заказов, которые сервер отказывается принимать по
        /// существу — в отличие от Reachable = false, это не про сеть.</summary>
        public HashSet<Guid> RefusedIds { get; } = new();

        public List<QueueOrder> Posted { get; } = new();
        public int PostOrderAsyncCallCount { get; set; }

        /// <summary>Что вернёт GetClosedAsync — тест сам решает, какие заказы
        /// считаются закрытыми на сервере.</summary>
        public List<QueueOrder> ClosedOrders { get; set; } = new();

        public int GetClosedAsyncCallCount { get; private set; }
        public int? LastRequestedTillIndex { get; private set; }

        public Task<PostOrderResult> PostOrderAsync(QueueOrder order)
        {
            PostOrderAsyncCallCount++;
            if (!Reachable) return Task.FromResult(PostOrderResult.Unreachable);
            if (RefusedIds.Contains(order.Id)) return Task.FromResult(PostOrderResult.Refused);
            // Идемпотентность живёт на сервере; здесь просто копим всё, что дошло,
            // чтобы тест увидел дубль, если клиент пошлёт его дважды.
            Posted.Add(order);
            return Task.FromResult(PostOrderResult.Sent);
        }

        public Task<IReadOnlyList<QueueOrder>> GetClosedAsync(int tillIndex)
        {
            GetClosedAsyncCallCount++;
            LastRequestedTillIndex = tillIndex;
            return Task.FromResult<IReadOnlyList<QueueOrder>>(ClosedOrders);
        }
    }

    /// <summary>IssueAsync, который никогда не возвращает номер — стоит в
    /// queue.db заблокированной или переполненной базы, которую EnqueueAsync
    /// обязан пережить, а не уронить продажу.</summary>
    private sealed class ThrowingPool : INumberPool
    {
        public Task<int> IssueAsync(Guid orderId) => throw new InvalidOperationException("queue.db is locked");
        public Task ReleaseAsync(int number, Guid orderId) => Task.CompletedTask;
    }

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");

    private static readonly Func<DateTime> Now = () => new DateTime(2026, 8, 31, 10, 0, 0);

    private static (QueueClient Client, FakeTransport Transport, NumberPool Pool) Build(string? db = null)
    {
        var storage = new QueueStorage(db ?? TempDb());
        var pool = new NumberPool(storage, 0, "secret", Now);
        var transport = new FakeTransport();
        return (new QueueClient(storage, pool, transport, tillIndex: 0, Now), transport, pool);
    }

    private static SaleReceiptData Sale() => new(
        new List<CartItem> { new() { Product = new Product { Name = "Coffee", Price = 12m }, Quantity = 2m } },
        24m, 0m, 24m);

    [Fact]
    public async Task AnOrderGetsANumberAndReachesTheServer()
    {
        var (client, transport, _) = Build();

        var order = await client.EnqueueAsync(Sale());

        Assert.NotNull(order);
        Assert.InRange(order.Number, 100, 999);
        Assert.Single(transport.Posted);
        Assert.Equal(order.Id, transport.Posted[0].Id);
    }

    [Fact]
    public async Task TheServerBeingDownStillYieldsANumber()
    {
        var (client, transport, _) = Build();
        transport.Reachable = false;

        var order = await client.EnqueueAsync(Sale());

        Assert.NotNull(order);
        Assert.InRange(order.Number, 100, 999);
        Assert.Empty(transport.Posted);
    }

    [Fact]
    public async Task WhatCouldNotBeSentIsSentWhenTheServerReturns()
    {
        var (client, transport, _) = Build();
        transport.Reachable = false;
        var first = await client.EnqueueAsync(Sale());
        var second = await client.EnqueueAsync(Sale());
        Assert.NotNull(first);
        Assert.NotNull(second);

        transport.Reachable = true;
        await client.FlushAsync();

        Assert.Equal(2, transport.Posted.Count);
        Assert.Contains(transport.Posted, o => o.Id == first.Id);
        Assert.Contains(transport.Posted, o => o.Id == second.Id);
    }

    [Fact]
    public async Task FlushingTwiceDoesNotSendTheSameOrderTwice()
    {
        var (client, transport, _) = Build();
        transport.Reachable = false;
        await client.EnqueueAsync(Sale());

        transport.Reachable = true;
        await client.FlushAsync();
        await client.FlushAsync();

        Assert.Single(transport.Posted);
    }

    /// <summary>Every earlier test reaches the delete-on-success line with
    /// Reachable = false, so none of them ever watched a successful enqueue
    /// remove its own outbox row. Without that delete, a healthy register
    /// outbox would grow without bound and every flush would resend
    /// everything it ever sold - this is the test that would catch that.</summary>
    [Fact]
    public async Task ASuccessfulEnqueueDoesNotStayInTheOutbox()
    {
        var (client, transport, _) = Build();

        await client.EnqueueAsync(Sale());
        await client.FlushAsync();

        Assert.Single(transport.Posted);
    }

    [Fact]
    public async Task TheBufferSurvivesARestart()
    {
        var db = TempDb();
        var (client, transport, _) = Build(db);
        transport.Reachable = false;
        var order = await client.EnqueueAsync(Sale());
        Assert.NotNull(order);

        var (reopened, secondTransport, _) = Build(db);
        await reopened.FlushAsync();

        Assert.Single(secondTransport.Posted);
        Assert.Equal(order.Id, secondTransport.Posted[0].Id);
    }

    [Fact]
    public async Task AFailureIssuingTheNumberReturnsNullInsteadOfThrowing()
    {
        var storage = new QueueStorage(TempDb());
        var transport = new FakeTransport();
        var client = new QueueClient(storage, new ThrowingPool(), transport, tillIndex: 0, Now);

        var order = await client.EnqueueAsync(Sale());

        Assert.Null(order);
    }

    /// <summary>Хранилище, которое падает на записи в буфер и на чтении счётчика
    /// буфера. Запертый или переполненный queue.db — ровно тот случай, ради
    /// которого EnqueueAsync (и, после Critical 1, PendingCountAsync) вообще
    /// ловят исключения, и единственный способ это проверить, не угадывая
    /// тайминги настоящего SQLite.</summary>
    private sealed class ThrowingStorage : IQueueStorage
    {
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<string?> GetStateAsync(string key) => Task.FromResult<string?>(null);
        public Task SetStateAsync(string key, string value) => Task.CompletedTask;

        public Task SaveOutboxAsync(Guid id, string kind, string payload)
            => throw new InvalidOperationException("queue.db is locked");

        public Task<IReadOnlyList<(Guid Id, string Payload)>> GetOutboxAsync(string kind)
            => Task.FromResult<IReadOnlyList<(Guid, string)>>(Array.Empty<(Guid, string)>());

        public Task<int> GetOutboxCountAsync(string kind)
            => throw new InvalidOperationException("queue.db is locked");

        public Task DeleteOutboxAsync(Guid id) => Task.CompletedTask;
        public Task MarkOutboxRejectedAsync(Guid id, string reason) => Task.CompletedTask;

        // Ничего из Task 13-15, Task 25 и Fix 3 этот тест не касается — QueueClient
        // не зовёт эти методы вовсе, они здесь только чтобы фейк остался валидной
        // реализацией расширенного интерфейса.
        public Task<IReadOnlyList<QueueOrder>> GetLiveOrdersAsync()
            => Task.FromResult<IReadOnlyList<QueueOrder>>(Array.Empty<QueueOrder>());

        public Task<IReadOnlyList<QueueOrder>> GetRecentlyClosedOrdersAsync(DateTime now)
            => Task.FromResult<IReadOnlyList<QueueOrder>>(Array.Empty<QueueOrder>());

        public Task PurgeOldClosedOrdersAsync(DateTime now) => Task.CompletedTask;

        public Task SaveOrderAsync(QueueOrder order, DateTime receivedAt) => Task.CompletedTask;
        public Task<QueueOrder?> GetOrderAsync(Guid id) => Task.FromResult<QueueOrder?>(null);
        public Task UpdateOrderStateAsync(QueueOrder order) => Task.CompletedTask;
        public Task CloseStaleOrdersAsync(DateTime now) => Task.CompletedTask;
    }

    /// <summary>Номер уже выдан, а буфер записать не удалось. Продажа всё равно
    /// доводится до конца: номер у клиента на руках, бумага вышла, и отдать
    /// кассиру исключение на уже прошедшей продаже — худшее из возможного.
    /// Заказ до сервера не доедет, и это осознанная потеря: выбор здесь между
    /// потерянным заказом и сорванной продажей.</summary>
    [Fact]
    public async Task AFailureWritingTheBufferStillCompletesTheSale()
    {
        var storage = new QueueStorage(TempDb());
        var pool = new NumberPool(storage, 0, "secret", Now);
        var client = new QueueClient(new ThrowingStorage(), pool, new FakeTransport(), tillIndex: 0, Now);

        var order = await client.EnqueueAsync(Sale());

        Assert.NotNull(order);
        Assert.InRange(order!.Number, 100, 999);
    }

    /// <summary>Critical 1. PendingCountAsync used to be a bare pass-through to SQLite —
    /// unlike EnqueueAsync right above, which already swallows and logs everything. It is
    /// called from PosViewModel.ProceedToPayAsync's payment callback, an async void
    /// lambda with no try in its body and no unhandled-exception handler anywhere in
    /// Program.cs — reached only after CreateExpenseDocumentDetailedAsync and
    /// PrintReceiptAsync have already run. A locked, full or corrupt queue.db threw an
    /// SqliteException straight through this method and out of that lambda, taking the
    /// whole process down after the money was taken and the receipt was printed. A count
    /// that cannot be read is a missing badge, not a dead till.</summary>
    [Fact]
    public async Task APendingCountReadFailureReturnsZeroInsteadOfThrowing()
    {
        var storage = new QueueStorage(TempDb());
        var pool = new NumberPool(storage, 0, "secret", Now);
        var client = new QueueClient(new ThrowingStorage(), pool, new FakeTransport(), tillIndex: 0, Now);

        var count = await client.PendingCountAsync();

        Assert.Equal(0, count);
    }

    /// <summary>A refusal is about this one order, not about the rest of the
    /// buffer - unlike Unreachable, it must not stop later orders from going
    /// out, and the refused row must not come back on a later flush either.</summary>
    [Fact]
    public async Task ARefusedOrderIsTakenOutOfRotationWithoutBlockingLaterOnes()
    {
        var (client, transport, _) = Build();
        transport.Reachable = false;
        var refused = await client.EnqueueAsync(Sale());
        var ok = await client.EnqueueAsync(Sale());
        Assert.NotNull(refused);
        Assert.NotNull(ok);

        transport.Reachable = true;
        transport.RefusedIds.Add(refused.Id);
        await client.FlushAsync();

        Assert.DoesNotContain(transport.Posted, o => o.Id == refused.Id);
        Assert.Contains(transport.Posted, o => o.Id == ok.Id);

        // Taken out of rotation for good, not merely skipped this once: even
        // once the server would accept it, it is not retried.
        transport.RefusedIds.Clear();
        await client.FlushAsync();
        Assert.DoesNotContain(transport.Posted, o => o.Id == refused.Id);
    }

    /// <summary>Unreachable, unlike Refused, is about the whole trip: the rest
    /// of the buffer will not go through the same call either, so flush must
    /// stop at the first one instead of working through the rest.</summary>
    [Fact]
    public async Task AnUnreachableServerStopsFlushingAtTheFirstOrder()
    {
        var (client, transport, _) = Build();
        transport.Reachable = false;
        await client.EnqueueAsync(Sale());
        await client.EnqueueAsync(Sale());

        transport.PostOrderAsyncCallCount = 0;
        await client.FlushAsync();

        Assert.Equal(1, transport.PostOrderAsyncCallCount);
    }

    [Fact]
    public async Task FlushAsksForClosedOrdersOnThisTillIndex()
    {
        var (client, transport, _) = Build();

        await client.FlushAsync();

        Assert.Equal(1, transport.GetClosedAsyncCallCount);
        Assert.Equal(0, transport.LastRequestedTillIndex);
    }

    /// <summary>The server lists every closed order for the till on every
    /// poll, and nothing deletes them - QueueFlushLoop calls FlushAsync every
    /// 15 seconds, so the same closed order is reported and released over and
    /// over, not once. This is the test that makes NumberPool cooldown bug
    /// visible from the client side: run against the unguarded ReleaseAsync,
    /// the repeat release re-stamps the cooldown anchor and the final assert
    /// below fails.
    ///
    /// Same order reported twice, not a re-issue in between — the release-by-identity
    /// guard makes this a no-op automatically (the first release already clears
    /// IssuedFor for that order, so the repeat matches no row), same as before Critical
    /// 2's fix. Contrast with AStaleClosedOrderReplayDoesNotFreeANumberIssuedToSomeoneElse
    /// below, where the number IS re-issued in between and the old guard actually failed.</summary>
    [Fact]
    public async Task RepeatedFlushesOfTheSameClosedOrderDoNotStallItsCooldown()
    {
        var (client, transport, pool) = Build();

        var issued = new List<int>();
        var issuedIds = new List<Guid>();
        for (var i = 0; i < 180; i++)
        {
            var id = Guid.NewGuid();
            issuedIds.Add(id);
            issued.Add(await pool.IssueAsync(id));
        }
        var target = issued[0];
        var targetId = issuedIds[0];

        transport.ClosedOrders = new List<QueueOrder> { new() { Id = targetId, Number = target } };
        await client.FlushAsync(); // real release, anchored at seq 180

        for (var i = 0; i < 10; i++) await pool.IssueAsync(Guid.NewGuid()); // seq -> 190

        await client.FlushAsync(); // stale repeat of the same closed order

        for (var i = 0; i < NumberPool.CooldownIssues - 10 - 1; i++) // seq 191..229
            Assert.NotEqual(target, await pool.IssueAsync(Guid.NewGuid()));

        Assert.Equal(target, await pool.IssueAsync(Guid.NewGuid())); // seq 230
    }

    /// <summary>Critical 2, reproduced through the actual code path where it happens in
    /// production: QueueClient.FlushAsync releasing numbers by whatever GetClosedAsync
    /// reports, against a server that never deletes a closed order. Mirrors the
    /// reviewer's own reproduction (issue, release, re-issue to someone else, replay the
    /// stale release, assert the live number does not move) — see
    /// NumberPoolTest.AStaleReleaseForAReissuedNumberDoesNotFreeALiveOrder for the same
    /// scenario at the pool level, including why the final assertion checks the
    /// NumberPool row directly rather than issuing more numbers and hoping one of them
    /// exposes it (with the slice this exhausted, the third, oldest-first
    /// SelectNumberToIssueAsync branch can legitimately keep handing out other
    /// already-issued numbers for a long stretch regardless of whether the guard bug
    /// fired, so it would not reliably distinguish the fixed guard from the broken one).</summary>
    [Fact]
    public async Task AStaleClosedOrderReplayDoesNotFreeANumberIssuedToSomeoneElse()
    {
        var db = TempDb();
        var (client, transport, _) = Build(db);

        // Exhaust the till's 180-number slice first, same reasoning as the cooldown
        // tests: with fresh numbers still available the pool would just hand one of
        // those out instead of recycling `first`'s number, and the scenario would not
        // fire at all.
        var orders = new List<QueueOrder>();
        for (var i = 0; i < 180; i++) orders.Add((await client.EnqueueAsync(Sale()))!);
        var first = orders[0];

        transport.ClosedOrders = new List<QueueOrder> { new() { Id = first.Id, Number = first.Number } };
        await client.FlushAsync(); // real release, anchored at the current seq

        for (var i = 0; i < NumberPool.CooldownIssues - 1; i++) await client.EnqueueAsync(Sale());
        var second = await client.EnqueueAsync(Sale()); // the CooldownIssues-th issue after release
        Assert.Equal(first.Number, second!.Number); // sanity: same ticket number, new customer
        Assert.True(await IsStillIssuedAsync(db, first.Number)); // sanity: it is live right now

        // The server never deletes closed orders, so `first`'s stale "closed" is
        // replayed on the next 15-second flush — transport.ClosedOrders still names it.
        await client.FlushAsync();

        // `second` is the live holder now; the stale replay for `first` must not free
        // the number out from under them before their own real close.
        Assert.True(await IsStillIssuedAsync(db, first.Number));
    }

    private static async Task<bool> IsStillIssuedAsync(string db, int number)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}");
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT IssuedSeq FROM NumberPool WHERE Number = $n";
        cmd.Parameters.AddWithValue("$n", number);
        var result = await cmd.ExecuteScalarAsync();
        return result != null && result != DBNull.Value;
    }
}
