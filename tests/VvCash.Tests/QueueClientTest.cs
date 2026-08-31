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

    /// <summary>A transport whose PostOrderAsync does not resolve until the test calls
    /// Release - the honest way to prove EnqueueAsync does not wait for the network hop
    /// (see the freeze-fix tests below), instead of asserting on how fast it happens to
    /// return, which would be flaky on a loaded machine. GetClosedAsync is not part of
    /// what these tests exercise, so it just answers empty.</summary>
    private sealed class BlockingTransport : IQueueTransport
    {
        private readonly TaskCompletionSource<PostOrderResult> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PostOrderResult> PostOrderAsync(QueueOrder order) => _gate.Task;

        public Task<IReadOnlyList<QueueOrder>> GetClosedAsync(int tillIndex) =>
            Task.FromResult<IReadOnlyList<QueueOrder>>(Array.Empty<QueueOrder>());

        /// <summary>Lets every PostOrderAsync call currently blocked on this transport
        /// return <paramref name="result"/>.</summary>
        public void Release(PostOrderResult result) => _gate.SetResult(result);
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

    /// <summary>EnqueueAsync followed by settling its own dispatched background send. This
    /// fix detached that send from EnqueueAsync (see QueueClient's remarks), which is exactly
    /// the point - but it also means the loop-heavy tests below, which used to get one order
    /// fully resolved before the next EnqueueAsync call even started for free, now need to
    /// ask for that sequencing explicitly, or two dispatched sends racing later flushes or
    /// each other could double-post an order and make an exact-count assertion flaky. See
    /// LastDispatchedSend's own docstring for why awaiting it, not a wall-clock wait, is
    /// what keeps this deterministic.</summary>
    private static async Task<QueueOrder?> EnqueueAndSettle(QueueClient client)
    {
        var order = await client.EnqueueAsync(Sale());
        await client.LastDispatchedSend!;
        return order;
    }

    [Fact]
    public async Task AnOrderGetsANumberAndReachesTheServer()
    {
        var (client, transport, _) = Build();

        var order = await client.EnqueueAsync(Sale());
        // This fix: the send itself is no longer awaited by EnqueueAsync (see its
        // remarks) - that is the whole point of the fix - so a test that wants to
        // look at what the transport received has to wait for the dispatched
        // background send to settle first. Awaiting the task QueueClient itself
        // handed back is the deterministic way to do that; see LastDispatchedSend's
        // own docstring for why this is preferred over polling wall-clock time.
        await client.LastDispatchedSend!;

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
        await client.LastDispatchedSend!; // let the (no-op) background send settle before the test exits

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
        var firstSend = client.LastDispatchedSend!;
        var second = await client.EnqueueAsync(Sale());
        var secondSend = client.LastDispatchedSend!;
        Assert.NotNull(first);
        Assert.NotNull(second);

        // Both background sends have to settle (as Unreachable no-ops, since
        // Reachable is still false here) before Reachable flips - otherwise
        // whichever of these two hasn't run yet could fire mid-flush, seeing
        // Reachable = true and posting straight to the transport out from
        // under FlushAsync, which would double-post that order.
        await Task.WhenAll(firstSend, secondSend);

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
        await client.LastDispatchedSend!; // settle the no-op send before Reachable flips - see the test above

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
        // Reachable = true here, so EnqueueAsync's own dispatched send will delete
        // this row itself. Settle it before FlushAsync walks the outbox, or the two
        // could both see the row and both post it - not wrong (the row still ends
        // up gone either way), but it would make the Assert.Single below flaky.
        await client.LastDispatchedSend!;
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
        await client.LastDispatchedSend!; // settle the no-op send before this client goes out of scope
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

        // Same failure as GetOutboxCountAsync above, and for the same reason:
        // PendingCountAsync now reads ids (see its own docstring on why a bare
        // count stopped being enough), not a count, so this is the method that
        // actually has to throw for APendingCountReadFailureReturnsZeroInsteadOfThrowing
        // below to still exercise the locked-db path it is named for.
        public Task<IReadOnlyList<Guid>> GetOutboxIdsAsync(string kind)
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
        var refusedSend = client.LastDispatchedSend!;
        var ok = await client.EnqueueAsync(Sale());
        var okSend = client.LastDispatchedSend!;
        Assert.NotNull(refused);
        Assert.NotNull(ok);

        // Settle both no-op sends before Reachable flips - see WhatCouldNotBeSentIsSentWhenTheServerReturns.
        await Task.WhenAll(refusedSend, okSend);

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
        var firstSend = client.LastDispatchedSend!;
        await client.EnqueueAsync(Sale());
        var secondSend = client.LastDispatchedSend!;
        // Both no-op sends have to be done before the counter is reset below - a
        // late one firing after the reset would inflate PostOrderAsyncCallCount by
        // a call that has nothing to do with FlushAsync.
        await Task.WhenAll(firstSend, secondSend);

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
        for (var i = 0; i < 180; i++) orders.Add((await EnqueueAndSettle(client))!);
        var first = orders[0];

        transport.ClosedOrders = new List<QueueOrder> { new() { Id = first.Id, Number = first.Number } };
        await client.FlushAsync(); // real release, anchored at the current seq

        for (var i = 0; i < NumberPool.CooldownIssues - 1; i++) await EnqueueAndSettle(client);
        var second = await EnqueueAndSettle(client); // the CooldownIssues-th issue after release
        Assert.Equal(first.Number, second!.Number); // sanity: same ticket number, new customer
        Assert.True(await IsStillIssuedAsync(db, first.Number)); // sanity: it is live right now

        // The server never deletes closed orders, so `first`'s stale "closed" is
        // replayed on the next 15-second flush — transport.ClosedOrders still names it.
        await client.FlushAsync();

        // `second` is the live holder now; the stale replay for `first` must not free
        // the number out from under them before their own real close.
        Assert.True(await IsStillIssuedAsync(db, first.Number));
    }

    // --- The freeze fix: a payment must not wait on the network hop to the queue server ---
    //
    // The shop reported the register freezing for a couple of seconds on every payment.
    // The timing probe used to confirm the diagnosis (see the report for this fix) showed
    // HttpQueueTransport.PostOrderAsync itself was the dominant cost: a genuinely
    // unreachable server measured ~2.0-2.1s per attempt on this machine (a refused loopback
    // TCP connection alone, well under HttpQueueTransport.RequestTimeout's 3s ceiling), and
    // EnqueueAsync used to await that call directly on every single sale. The three tests
    // below are what would fail without the fix and pass with it.

    /// <summary>The actual regression test for the freeze: EnqueueAsync must return without
    /// waiting for the transport call to settle. BlockingTransport never resolves
    /// PostOrderAsync on its own, so if EnqueueAsync still awaited the send inline (the bug),
    /// this would hang against an unreleased gate until the safety-net delay below wins the
    /// race and the Assert.Same fails — not a wall-clock threshold on how fast EnqueueAsync
    /// runs, which is exactly what the brief for this fix asks to avoid, just a generous
    /// upper bound so a regression fails the test instead of hanging the run.</summary>
    [Fact]
    public async Task EnqueueDoesNotWaitForTheServerRoundTrip()
    {
        var storage = new QueueStorage(TempDb());
        var pool = new NumberPool(storage, 0, "secret", Now);
        var transport = new BlockingTransport();
        var client = new QueueClient(storage, pool, transport, tillIndex: 0, Now);

        var enqueueTask = client.EnqueueAsync(Sale());

        var hangGuard = Task.Delay(TimeSpan.FromSeconds(10));
        var winner = await Task.WhenAny(enqueueTask, hangGuard);

        // The guarantee under test: EnqueueAsync completed on its own, while the transport
        // call it dispatched is still outstanding - not because anything answered it.
        Assert.Same(enqueueTask, winner);
        Assert.True(enqueueTask.IsCompletedSuccessfully);

        var order = await enqueueTask;
        Assert.NotNull(order);
        Assert.InRange(order!.Number, 100, 999); // the number is still issued, transport or no transport

        // Let the background send settle before the test exits, so it doesn't leak into the next one.
        transport.Release(PostOrderResult.Sent);
        await client.LastDispatchedSend!;
    }

    /// <summary>The durability guarantee this fix must not weaken: the order has to be on
    /// disk before EnqueueAsync returns, independent of whether — or when — its send ever
    /// resolves. Also exercises PendingCountAsync's own half of the fix (see its docstring):
    /// the badge has to count this order as pending for as long as its send is genuinely
    /// unresolved, which BlockingTransport lets this test hold open indefinitely rather than
    /// relying on the background task happening to still be running at assertion time.</summary>
    [Fact]
    public async Task TheOrderIsDurableAndCountedPendingBeforeItsSendEverResolves()
    {
        var storage = new QueueStorage(TempDb());
        var pool = new NumberPool(storage, 0, "secret", Now);
        var transport = new BlockingTransport();
        var client = new QueueClient(storage, pool, transport, tillIndex: 0, Now);

        var order = await client.EnqueueAsync(Sale());
        Assert.NotNull(order);

        // The send is still outstanding - transport.Release has not been called - yet the
        // row is already durable, and the badge already counts it.
        var direct = await storage.GetOutboxAsync("Order");
        Assert.Contains(direct, row => row.Id == order!.Id);
        Assert.Equal(1, await client.PendingCountAsync());

        transport.Release(PostOrderResult.Sent);
        await client.LastDispatchedSend!;

        // Settled now: the send succeeded, so the row is gone and the badge drops back to 0.
        Assert.Equal(0, await client.PendingCountAsync());
    }

    /// <summary>The other existing guarantee this fix must not weaken: a send that does not
    /// go through leaves the row for QueueFlushLoop to retry — unchanged from before this
    /// fix, just no longer on the sale's own thread. TheServerBeingDownStillYieldsANumber
    /// above already covers the immediate-Unreachable case (FakeTransport); this one is the
    /// same guarantee through the code path this fix actually changed — a send resolved in
    /// the background, after EnqueueAsync already returned.</summary>
    [Fact]
    public async Task AFailedBackgroundSendLeavesTheRowForFlushAsync()
    {
        var storage = new QueueStorage(TempDb());
        var pool = new NumberPool(storage, 0, "secret", Now);
        var transport = new BlockingTransport();
        var client = new QueueClient(storage, pool, transport, tillIndex: 0, Now);

        var order = await client.EnqueueAsync(Sale());
        Assert.NotNull(order);

        transport.Release(PostOrderResult.Unreachable);
        await client.LastDispatchedSend!;

        Assert.Equal(1, await client.PendingCountAsync());
        var direct = await storage.GetOutboxAsync("Order");
        Assert.Contains(direct, row => row.Id == order!.Id);
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
