using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Queue;

/// <summary>Постановка заказа кассой-клиентом с локальным буфером на случай
/// недоступного сервера. См. IQueueClient и QueueClientTest — fail-open
/// решение спеки: продажа не встаёт из-за сети никогда.
///
/// «Никогда» распространяется и на саму базу очереди. Task 22 зовёт
/// EnqueueAsync внутри ProceedToPayAsync уже после того, как чек ушёл в
/// печать, и до того, как корзина очищена — так что SqliteException из
/// IssueAsync/SaveOutboxAsync (запертый или переполненный queue.db) здесь же
/// и гасится, тем же приёмом, что отказ принтера в остальном коде: залогировать
/// и продолжить. Бросить исключение наружу значило бы показать кассиру ошибку
/// на продаже, которая на самом деле уже прошла, да ещё и с корзиной, которая
/// не очистится.</summary>
public class QueueClient : IQueueClient
{
    /// <summary>Kind записи буфера для заказов. Отдельная константа, потому что
    /// позже в тот же QueueOutbox лягут записи смены состояния под другим Kind
    /// — Column и была заведена ради этого (см. докстринг таблицы в
    /// QueueStorage).</summary>
    private const string OrderKind = "Order";

    /// <summary>Причина, которую видит буфер при осознанном отказе сервера.
    /// IQueueTransport сегодня не возвращает текст ошибки — только сам факт
    /// отказа (см. PostOrderResult) — так что здесь честнее записать общую
    /// фразу, чем выдумывать подробности, которых нет. Когда у транспорта
    /// появится настоящая причина, эта константа и станет тем местом, где её
    /// заменить.</summary>
    private const string RefusedReason = "Сервер отказал в приёме заказа.";

    /// <summary>Интерфейс, а не конкретный QueueStorage: клиенту нужны только
    /// четыре метода буфера, и ни один из них не требует ConnectionString — за
    /// ним к классу ходит NumberPool, а не эта служба. Практическая цена
    /// конкретного типа была в том, что «запись в буфер упала» становилась
    /// непроверяемой: подсунуть падающее хранилище некуда, а ронять настоящий
    /// SQLite ради теста — гадание по таймингам.</summary>
    private readonly IQueueStorage _storage;
    private readonly INumberPool _pool;
    private readonly IQueueTransport _transport;
    private readonly int _tillIndex;
    private readonly Func<DateTime> _now;

    /// <summary>Ids of orders whose durable outbox row exists but whose own background send
    /// (dispatched by EnqueueAsync below — see its remarks) has not yet resolved that row
    /// (deleted it on Sent, marked it on Refused, or given up on Unreachable/failure). Read
    /// only by PendingCountAsync, to close the exact race its own docstring describes; see
    /// there for why plain outbox membership is not enough on its own once the send is not
    /// awaited by the sale path anymore. Per-instance, not persisted — there is one
    /// QueueClient per till for the life of the process, and nothing outside this process
    /// needs to know about a send that has not settled yet (see QueueFlushLoop, which will
    /// simply retry from the outbox on its own schedule regardless).</summary>
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    /// <summary>Testing hook only — internal, not part of IQueueClient, and not touched by
    /// production code. EnqueueAsync no longer awaits its own send (see its remarks), so a
    /// test that needs to observe the send's outcome deterministically — not by polling a
    /// wall clock, which the freeze-fix's test brief explicitly rules out — awaits this
    /// instead. Sequential test to sequential test contamination is not a concern: nothing
    /// reads this except with fresh QueueClient instances (see QueueClientTest.Build).</summary>
    internal Task? LastDispatchedSend { get; private set; }

    public QueueClient(IQueueStorage storage, INumberPool pool, IQueueTransport transport, int tillIndex, Func<DateTime> now)
    {
        _storage = storage;
        _pool = pool;
        _transport = transport;
        _tillIndex = tillIndex;
        _now = now;
    }

    /// <summary>Issues a queue number for a till whose QueueRole is Off but that still
    /// has a ticket or kitchen-order printer configured (see PosViewModel's
    /// ProceedToPayAsync remarks on why that is a working, common configuration): the
    /// printers need a number to put on paper, but there is nothing to enqueue an order
    /// into — no server for it to reach, no outbox anything would ever drain (Off leaves
    /// QueueFlushLoop unstarted — see App.axaml.cs). Mints its own throwaway order id
    /// purely so NumberPool has something to stamp IssuedFor with (see NumberPool's own
    /// docstring on why that identity matters); nothing outside this call ever learns
    /// that id, so the number simply sits issued until the pool's cooldown/exhaustion
    /// branches recycle it — the same degenerate-but-expected fate the design doc
    /// describes for a kitchen screen that never closes anything.
    ///
    /// Same fail-open swallow as EnqueueAsync's own number step, sharing its
    /// implementation via TryIssueNumberAsync below.</summary>
    public Task<int?> IssueNumberAsync() => TryIssueNumberAsync(Guid.NewGuid());

    public async Task<QueueOrder?> EnqueueAsync(SaleReceiptData sale)
    {
        // Minted before the number is asked for, not after: NumberPool.IssueAsync needs
        // the order's own id to stamp NumberPool.IssuedFor with (see its docstring for
        // why that identity is what makes ReleaseAsync safe against a stale replay), so
        // the id has to exist before that call, not after it the way this used to read.
        var orderId = Guid.NewGuid();
        var number = await TryIssueNumberAsync(orderId);
        if (number == null)
        {
            // Без номера заказу не бывать — нечего ни буферизовать, ни
            // отправлять. Продажа всё равно не встанет: чек и талон печатаются
            // независимо от очереди (см. класс-докстринг), а вызывающему здесь
            // просто нечего показать на экране кухни.
            return null;
        }

        var order = new QueueOrder
        {
            Id = orderId,
            Number = number.Value,
            TillIndex = _tillIndex,
            State = QueueOrderState.New,
            CreatedAt = _now(),
            SaleDocumentNumber = sale.DocumentNumber ?? string.Empty,
            Lines = sale.Items.Select(item => new QueueOrderLine
            {
                Name = item.Product.Name,
                Quantity = item.QuantityDisplay
            }).ToList()
        };

        try
        {
            // Буфер сначала, отправка потом: падение между «отправлено» и
            // «записано» потеряло бы заказ, а дубль отправки сервер просто
            // отбросит по Guid (см. FakeTransport в тесте). This is the
            // durability guarantee, and this fix does not touch it: the row
            // is on disk before EnqueueAsync can return, whether or not the
            // send below ever gets dispatched.
            await _storage.SaveOutboxAsync(order.Id, OrderKind, JsonSerializer.Serialize(order));
        }
        catch (Exception ex)
        {
            // Номер уже выдан — сгоревший номер безобиден (одно пропущенное
            // место в обороте пула), а вот несостоявшаяся продажа — нет.
            // Отдаём заказ таким, какой он есть: без строки в буфере
            // FlushAsync его не досошлёт, но чек и талон это не остановит.
            Console.WriteLine($"[QueueClient] Could not buffer queue order {order.Id}: {ex.GetType().Name}: {ex.Message}");
            return order;
        }

        // Everything the sale itself needed is already done: the number was issued before
        // this method was even called, the paper is printed from it independently of
        // everything below (see ProceedToPayAsync), and the order is now durably on disk —
        // QueueFlushLoop will retry it every 15 seconds even if the process died right
        // here. The one thing left, telling the neighbour register, is a real HTTP request
        // (HttpQueueTransport.RequestTimeout: up to 3 seconds per attempt) that nothing on
        // the sale path is waiting for — so it must not be awaited here. This is the actual
        // fix for the freeze the shop reported: measured, an unreachable queue server used
        // to make this call itself take ~2.1s (dominated by the transport's own ~2.0s), and
        // now the caller never sees that wait at all.
        //
        // Registered in _inFlight before Task.Run is even dispatched, not after: see
        // PendingCountAsync's docstring for why the ordering here — durable write, then
        // _inFlight, then dispatch — is what makes the badge it reads deterministic rather
        // than a race against the thread pool.
        _inFlight[order.Id] = 0;

        // Task.Run, the same shape QueueFlushLoop.Start uses for its own loop, not a bare
        // unawaited call to SendAndRecordAsync(order): without it, this async method would
        // capture whatever synchronization context called EnqueueAsync (Avalonia's UI
        // dispatcher, in production) and its awaits would keep hopping back onto that
        // thread to resume — exactly the coupling to the interactive path this fix removes
        // elsewhere. `_ =`, not stored anywhere a caller could await it and not collected by
        // anything with its own lifecycle (QueueClient owns no CancellationTokenSource to
        // tear it down mid-flight) — SendAndRecordAsync's own try/catch is what keeps a
        // failure here from becoming an unobserved task exception, the same division of
        // responsibility QueueFlushLoop.Start uses for its loop.
        var send = Task.Run(() => SendAndRecordAsync(order));
        LastDispatchedSend = send;
        _ = send;

        return order;
    }

    /// <summary>The network half of EnqueueAsync, run detached from the sale path — see the
    /// dispatch site above for why. Sent/Refused/Unreachable are resolved exactly the way
    /// EnqueueAsync used to resolve them inline before this fix; only "the caller is not
    /// waiting for it" changed.
    ///
    /// A QueueFlushLoop tick can legitimately be walking the very same outbox row at the
    /// same time — it polls every 15 seconds, and this row is visible to it the moment
    /// SaveOutboxAsync's transaction above commits, well before this task is even
    /// guaranteed to have started running. That race is not something this method needs to
    /// prevent, because both possible bookkeeping calls are plain, idempotent
    /// `WHERE Id = ...` statements (see QueueStorage.DeleteOutboxAsync and
    /// MarkOutboxRejectedAsync): whichever of the two racers finishes last simply repeats a
    /// no-op against a row the other one already resolved — a DELETE that matches nothing,
    /// or an UPDATE that matches nothing. Nothing here can lose the row: at every instant it
    /// is either still sitting in the outbox for the next flush, or it has been resolved
    /// (deleted or marked) by whichever attempt got there first. The one thing both racers
    /// CAN do is post the same order to the transport twice — already an accepted outcome
    /// per this class's own docstring (the server dedupes by Guid), and no different from
    /// what a slow send racing a 15-second flush could already do before this fix.</summary>
    private async Task SendAndRecordAsync(QueueOrder order)
    {
        try
        {
            var result = await _transport.PostOrderAsync(order);
            if (result == PostOrderResult.Sent)
            {
                await _storage.DeleteOutboxAsync(order.Id);
            }
            else if (result == PostOrderResult.Refused)
            {
                await _storage.MarkOutboxRejectedAsync(order.Id, RefusedReason);
            }
            // Unreachable — строка остаётся в буфере как есть, до ближайшего FlushAsync.
        }
        catch (Exception ex)
        {
            // Same treatment as every other queue.db failure in this class, and doubly so
            // here: a background task that throws unobserved must not take the process
            // down, and there is nobody left to show an error to anyway — the sale this
            // send belongs to finished and returned long ago. The row, if it was not
            // already resolved above, is simply left where it is; FlushAsync gets the next
            // attempt at it.
            Console.WriteLine($"[QueueClient] Background send for queue order {order.Id} failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Only after Sent/Refused/Unreachable has actually been acted on (or given up
            // on, just above) — see PendingCountAsync's docstring for why this specific
            // ordering, resolve-then-leave-_inFlight, is what keeps the badge from ever
            // under-counting a still-pending order.
            _inFlight.TryRemove(order.Id, out _);
        }
    }

    public async Task FlushAsync()
    {
        var outbox = await _storage.GetOutboxAsync(OrderKind);
        foreach (var (id, payload) in outbox)
        {
            QueueOrder? order;
            try
            {
                order = JsonSerializer.Deserialize<QueueOrder>(payload);
            }
            catch (JsonException)
            {
                order = null;
            }

            if (order == null)
            {
                // Не разбирается — не заблокирует очередь навсегда: со строкой,
                // которую уже никогда не прочитать, ждать нечего.
                await _storage.DeleteOutboxAsync(id);
                continue;
            }

            var result = await _transport.PostOrderAsync(order);

            if (result == PostOrderResult.Unreachable)
            {
                // Недоступность — это не про этот заказ, а про весь рейс:
                // следующие всё равно не дойдут тем же вызовом, дальше не идём.
                break;
            }

            if (result == PostOrderResult.Refused)
            {
                // Отказ — только про этот заказ; соседи в буфере тут ни при
                // чём, поэтому, в отличие от Unreachable, цикл продолжается.
                await _storage.MarkOutboxRejectedAsync(id, RefusedReason);
                continue;
            }

            await _storage.DeleteOutboxAsync(id);
        }

        // Только заказы этой кассы: чужой номер живёт в чужом пуле. closed.Id, not just
        // closed.Number: the server reports the same closed order again on every poll
        // within its retention window (Fix 3 bounded that window and now purges old
        // closed orders outright — see QueueStorage.RecentlyClosedWindow/ClosedOrderRetention
        // — but within the window a replay is still routine, every 15 seconds), so
        // ReleaseAsync has to be told WHICH order is asking, to tell a genuine close
        // apart from a stale replay for a number already re-issued to someone else —
        // see NumberPool.ReleaseAsync's own docstring for the collision this used to allow.
        foreach (var closed in await _transport.GetClosedAsync(_tillIndex))
        {
            await _pool.ReleaseAsync(closed.Number, closed.Id);
        }
    }

    /// <summary>Shared by IssueNumberAsync and EnqueueAsync: issue a number for
    /// <paramref name="orderId"/>, swallowing (logging, not throwing) a queue.db that is
    /// locked, full or corrupt — the one thing every caller of this needs, since none of
    /// them has anything to show for a number that could not be issued.</summary>
    private async Task<int?> TryIssueNumberAsync(Guid orderId)
    {
        try
        {
            return await _pool.IssueAsync(orderId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QueueClient] Could not issue a queue number: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Same treatment as EnqueueAsync's own catches above, and for the same
    /// reason: a locked, full or corrupt queue.db must not throw out of here. This one
    /// matters even more than EnqueueAsync's — it is called from an async-void payment
    /// callback (PosViewModel's ProceedToPayAsync -> MixedPaymentViewModel's completion
    /// lambda) with no try around it and no unhandled-exception handler anywhere in
    /// Program.cs, so an uncaught SqliteException here reaches Avalonia's synchronization
    /// context and takes the whole process down — after the money is taken and the
    /// receipt is printed, before the cart is cleared. A count that cannot be read is a
    /// missing badge, not a dead till.
    ///
    /// A bare outbox count stopped being enough once EnqueueAsync's send moved off the sale
    /// path (see its remarks): ProceedToPayAsync calls this on the very next line after
    /// EnqueueAsync returns, to refresh the badge, and by then the row that call just wrote
    /// may already be gone — deleted by that same order's own background send, which needs
    /// nothing more than a thread-pool pickup and a successful POST to get there before this
    /// method's own SELECT runs. Which side of that wins is thread-pool scheduling, not a
    /// decision anyone made — exactly the "accidental" outcome not wanted here.
    ///
    /// The decision: a durably-buffered order counts as pending for the badge until its own
    /// send has actually been resolved (deleted, marked, or given up on) — not merely until
    /// its row happens to still be sitting in the outbox at whatever instant this runs. See
    /// _inFlight: EnqueueAsync adds an order's id there before it ever dispatches that
    /// order's send, and SendAndRecordAsync only removes it in its own finally, after the row
    /// has already been resolved one way or another. So every order is accounted for
    /// continuously — via the outbox row, via _inFlight, or (briefly, harmlessly) via both at
    /// once — from the moment it is durably saved to the moment its send is fully settled,
    /// with no gap either side could fall through. Unioning the two sets (not simply adding
    /// their sizes) is what keeps a row that is in both from being counted twice.</summary>
    public async Task<int> PendingCountAsync()
    {
        try
        {
            var outboxIds = await _storage.GetOutboxIdsAsync(OrderKind);
            var pending = new HashSet<Guid>(outboxIds);
            foreach (var id in _inFlight.Keys)
            {
                pending.Add(id);
            }
            return pending.Count;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QueueClient] Could not read the pending queue count: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }
}
