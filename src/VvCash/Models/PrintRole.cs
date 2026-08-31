using System;
using System.Text.Json.Serialization;

namespace VvCash.Models;

/// <summary>Какие документы печатает конкретный принтер. Набор, а не одно
/// значение: точка ставит один принтер на чеки, второй на талоны, третий на
/// кухню — но с тем же успехом сажает всё на один аппарат.
///
/// Сериализуется именами, а не числом: settings.json на точках правят руками,
/// и "Receipt, Ticket" там читается, а 3 — нет.</summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrintRole
{
    None = 0,
    Receipt = 1,
    Ticket = 2,
    KitchenOrder = 4
}
