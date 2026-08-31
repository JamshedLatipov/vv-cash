using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Queue;

/// <summary>Постановка заказа кассой-клиентом с локальным буфером на случай
/// недоступного сервера. См. IQueueClient и QueueClientTest — fail-open
/// решение спеки: продажа не встаёт из-за сети никогда.</summary>
public class QueueClient : IQueueClient
{
    /// <summary>Kind записи буфера для заказов. Отдельная константа, потому что
    /// позже в тот же QueueOutbox лягут записи смены состояния под другим Kind
    /// — Column и была заведена ради этого (см. докстринг таблицы в
    /// QueueStorage).</summary>
    private const string OrderKind = "Order";

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

    public async Task<QueueOrder> EnqueueAsync(SaleReceiptData sale)
    {
        var order = new QueueOrder
        {
            Id = Guid.NewGuid(),
            Number = await _pool.IssueAsync(),
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

        // Буфер сначала, отправка потом: падение между «отправлено» и
        // «записано» потеряло бы заказ, а дубль отправки сервер просто
        // отбросит по Guid (см. FakeTransport в тесте).
        await _storage.SaveOutboxAsync(order.Id, OrderKind, JsonSerializer.Serialize(order));

        if (await _transport.PostOrderAsync(order))
        {
            await _storage.DeleteOutboxAsync(order.Id);
        }

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

            if (!await _transport.PostOrderAsync(order))
            {
                // Сервер недоступен — остальные всё равно не дойдут тем же
                // рейсом, дальше не идём.
                break;
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
