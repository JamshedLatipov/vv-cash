using System.Collections.Generic;

namespace VvCash.Models;

/// <summary>Аргументы чека одним объектом. Заведён ради бегунка: он печатает тот
/// же документ, и повторять десять параметров в третий раз — способ разойтись с
/// чеком на первой же правке.
///
/// PrintReceiptAsync намеренно оставлен со своим прежним списком параметров.
/// Переписать его — значит тронуть возвраты, обмены и три вью-модели ради
/// нулевого выигрыша; новый код берёт запись, старый остаётся как есть.</summary>
/// <param name="QueueNumber">Номер бегунка на кухню. Пусто на клиентском чеке —
/// блок с подстановкой {queue} тогда не печатается, ровно как решено спекой.</param>
/// <remarks>Заполняется PrintKitchenOrderAsync через
/// <c>sale with { QueueNumber = queueNumber }</c> перед вызовом
/// BuildSaleReceipt(EscPosCodePage, SaleReceiptData, ReceiptTemplate?) — не
/// раскладкой записи на десять позиционных аргументов и сборкой новой такой
/// же внутри BuildSaleReceipt, как было раньше: тот путь читал явный параметр
/// queueNumber и вообще не заглядывал в это поле записи, так что любое
/// значение, уже лежавшее в QueueNumber у переданного sale, молча
/// перебивалось. Пустая строка по-прежнему читается правилом пустой
/// подстановки как «поле не заполнено», без ошибки и без строки в лог, ровно
/// как для DocumentNumber у офлайновой продажи.</remarks>
public sealed record SaleReceiptData(
    IReadOnlyList<CartItem> Items,
    decimal Subtotal,
    decimal Discount,
    decimal Total,
    string? DiscountName = null,
    string? DocumentNumber = null,
    string? WarehouseName = null,
    string? SellerName = null,
    string? SaleDate = null,
    string? QueueNumber = null);
