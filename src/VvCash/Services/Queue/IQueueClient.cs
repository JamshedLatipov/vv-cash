using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Queue;

/// <summary>Постановка заказа кассой-клиентом. Fail-open: сервер лежит — номер
/// всё равно выдан, бумага всё равно вышла, заказ лёг в локальный буфер —
/// продажа не встаёт никогда, это решение спеки (см. QueueClientTest).</summary>
public interface IQueueClient
{
    /// <summary>Выдаёт номер, пишет заказ локально и пробует отправить его на
    /// сервер. Неудачная отправка не отменяет ни номер, ни заказ — именно в этом
    /// и есть fail-open: касса без сети должна пробить продажу так же, как
    /// касса с сетью.</summary>
    Task<QueueOrder> EnqueueAsync(SaleReceiptData sale);

    /// <summary>Досылает буфер и возвращает номера закрытых заказов этой кассы
    /// в пул.</summary>
    Task FlushAsync();
}
