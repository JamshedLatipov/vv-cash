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

    private readonly QueueStorage _storage;
    private readonly INumberPool _pool;
    private readonly IQueueTransport _transport;
    private readonly int _tillIndex;
    private readonly Func<DateTime> _now;

    public QueueClient(QueueStorage storage, INumberPool pool, IQueueTransport transport, int tillIndex, Func<DateTime> now)
    {
        _storage = storage;
        _pool = pool;
        _transport = transport;
        _tillIndex = tillIndex;
        _now = now;
    }

    public async Task<QueueOrder?> EnqueueAsync(SaleReceiptData sale)
    {
        int number;
        try
        {
            number = await _pool.IssueAsync();
        }
        catch (Exception ex)
        {
            // Без номера заказу не бывать — нечего ни буферизовать, ни
            // отправлять. Продажа всё равно не встанет: чек и талон печатаются
            // независимо от очереди (см. класс-докстринг), а вызывающему здесь
            // просто нечего показать на экране кухни.
            Console.WriteLine($"[QueueClient] Could not issue a queue number: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        var order = new QueueOrder
        {
            Id = Guid.NewGuid(),
            Number = number,
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

        // Только заказы этой кассы: чужой номер живёт в чужом пуле.
        foreach (var closed in await _transport.GetClosedAsync(_tillIndex))
        {
            await _pool.ReleaseAsync(closed.Number);
        }
    }
}
