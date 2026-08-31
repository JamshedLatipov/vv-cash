using System;
using System.Collections.Generic;

namespace VvCash.Models;

/// <summary>Заказ очереди. Id — GUID самого заказа, присвоенный кассой в момент
/// постановки: по нему сервер узнаёт повтор при досыле буфера, поэтому он и есть
/// ключ идемпотентности. Не GUID кассы — тот был бы одним и тем же на каждом
/// заказе с этой кассы и схлопнул бы дедупликацию на сервере в один заказ.
///
/// SaleDocumentNumber пуст у продажи, пробитой без интернета: номер документа
/// придёт с бэкенда позже, и ни печать, ни экраны от него не зависят.</summary>
public class QueueOrder
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public int TillIndex { get; set; }
    public QueueOrderState State { get; set; } = QueueOrderState.New;
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadyAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string SaleDocumentNumber { get; set; } = string.Empty;
    public List<QueueOrderLine> Lines { get; set; } = new();
}

public class QueueOrderLine
{
    public string Name { get; set; } = string.Empty;
    public string Quantity { get; set; } = string.Empty;
}
