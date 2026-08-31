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
        public Task<int> IssueAsync() => throw new InvalidOperationException("queue.db is locked");
        public Task ReleaseAsync(int number) => Task.CompletedTask;
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
    /// below fails.</summary>
    [Fact]
    public async Task RepeatedFlushesOfTheSameClosedOrderDoNotStallItsCooldown()
    {
        var (client, transport, pool) = Build();

        var issued = new List<int>();
        for (var i = 0; i < 180; i++) issued.Add(await pool.IssueAsync());
        var target = issued[0];

        transport.ClosedOrders = new List<QueueOrder> { new() { Number = target } };
        await client.FlushAsync(); // real release, anchored at seq 180

        for (var i = 0; i < 10; i++) await pool.IssueAsync(); // seq -> 190

        await client.FlushAsync(); // stale repeat of the same closed order

        for (var i = 0; i < NumberPool.CooldownIssues - 10 - 1; i++) // seq 191..229
            Assert.NotEqual(target, await pool.IssueAsync());

        Assert.Equal(target, await pool.IssueAsync()); // seq 230
    }
}
