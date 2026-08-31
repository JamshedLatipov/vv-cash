using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Queue;

/// <summary>Разговор кассы-клиента с кассой-сервером. Отдельным интерфейсом,
/// чтобы поведение при недоступном сервере проверялось без сокета: отказ
/// соединения к закрытому порту loopback на этой машине занимает ~2.2 с и
/// превращает такие тесты в минутные.</summary>
public interface IQueueTransport
{
    /// <summary>false — сервер недоступен. Не исключение: недоступный сервер это
    /// штатное состояние, а не ошибка.</summary>
    Task<bool> PostOrderAsync(QueueOrder order);

    /// <summary>Закрытые заказы этой кассы — чтобы вернуть их номера в пул.</summary>
    Task<IReadOnlyList<QueueOrder>> GetClosedAsync(int tillIndex);
}
