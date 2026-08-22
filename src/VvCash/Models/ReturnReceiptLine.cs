namespace VvCash.Models;

/// <summary>Строка чека возврата и обмена. Quantity — decimal, потому что выданная
/// сторона обмена может быть дробной (1.4 кг). Возвращаемая сторона честно целая:
/// ReturnLineVm.ReturnQty — int, серверный ReturnLineRequest.Quantity тоже, так что
/// decimal покрывает оба случая без выдумывания дробных возвратов.</summary>
public record ReturnReceiptLine(string Name, decimal Quantity, decimal LineRefund);
