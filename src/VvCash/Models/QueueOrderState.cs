namespace VvCash.Models;

public enum QueueOrderState
{
    New,
    InProgress,
    Ready,
    Closed,
    Cancelled
}

/// <summary>Допустимые переходы. Отдельно от модели и без исключений внутри:
/// решение «можно ли» принимает сервер по приходящему запросу, а не заказ
/// сам о себе.</summary>
public static class QueueOrderStates
{
    /// <summary>Вперёд по цепочке — по одному шагу; отмена — с любого рабочего
    /// состояния. Закрытый и отменённый — конечные: кухонный экран с задержкой на
    /// сети не должен уметь «оживить» выданный заказ повторным нажатием.</summary>
    public static bool CanMove(QueueOrderState from, QueueOrderState to) => (from, to) switch
    {
        (QueueOrderState.New, QueueOrderState.InProgress) => true,
        (QueueOrderState.InProgress, QueueOrderState.Ready) => true,
        (QueueOrderState.Ready, QueueOrderState.Closed) => true,
        (QueueOrderState.New or QueueOrderState.InProgress or QueueOrderState.Ready,
            QueueOrderState.Cancelled) => true,
        _ => false
    };
}
