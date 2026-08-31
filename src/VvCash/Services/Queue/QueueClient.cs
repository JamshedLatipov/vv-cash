using System;
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
            // отбросит по Guid (см. FakeTransport в тесте).
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

        return order;
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
    /// missing badge, not a dead till.</summary>
    public async Task<int> PendingCountAsync()
    {
        try
        {
            return await _storage.GetOutboxCountAsync(OrderKind);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QueueClient] Could not read the pending queue count: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }
}
