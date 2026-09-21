using System;
using System.Collections.Generic;
using System.Globalization;

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

    /// <summary>То, что видит клиент: префикс кассы и число, как есть, без
    /// разделителя — разделитель, если нужен, часть префикса («A-»).
    /// Invariant: на этой машине ru-RU, и ToString() без культуры однажды
    /// напечатает не то.</summary>
    public static string FormatLabel(string prefix, int number) =>
        prefix + number.ToString(CultureInfo.InvariantCulture);
}

public class QueueOrderLine
{
    public string Name { get; set; } = string.Empty;
    public string Quantity { get; set; } = string.Empty;
}
