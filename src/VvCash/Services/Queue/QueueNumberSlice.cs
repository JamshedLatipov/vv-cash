using System;
using System.Linq;
using VvCash.Models;

namespace VvCash.Services.Queue;

/// <summary>Какие числа диапазона принадлежат этой кассе. Единственное место
/// с формулой: NumberPool раскладывает по ней пул, а предпросмотр в
/// настройках показывает по ней первый и последний номер — и они обязаны
/// совпадать.
///
/// Два правила, а не одно: при перемешивании срез по остатку
/// ((n − Min) % TillCount == TillIndex) — при 100–999 на 5 касс это в
/// точности прежнее n % 5, так что обновление не сталкивает кассы, которые
/// обновились в разное время одного дня. Без перемешивания — сплошные блоки
/// по ceil(count / TillCount), чтобы номера шли 1, 2, 3, а не 1, 3, 5: здесь
/// вид среза виден клиенту, а при шаффле — нет.</summary>
public static class QueueNumberSlice
{
    /// <summary>Номера этой кассы по возрастанию. Пусто, если срез пуст
    /// (касса за концом короткого диапазона) или настройки вырождены — не
    /// бросает: настройки нормализует SettingsService, а тесты и предпросмотр
    /// могут собрать record напрямую.</summary>
    public static int[] Ascending(QueueNumberOptions o)
    {
        var count = o.Max - o.Min + 1;
        if (count <= 0 || o.TillCount < 1 || o.TillIndex < 0) return Array.Empty<int>();

        var range = Enumerable.Range(o.Min, count);
        if (o.Shuffle)
        {
            return range.Where(n => (n - o.Min) % o.TillCount == o.TillIndex).ToArray();
        }

        var block = (count + o.TillCount - 1) / o.TillCount;
        return range.Where(n => (n - o.Min) / block == o.TillIndex).ToArray();
    }

    /// <summary>Первый и последний номер среза с префиксом и их число — для
    /// строки предпросмотра в настройках. Null для пустого среза.</summary>
    public static (string First, string Last, int Count)? Summarize(QueueNumberOptions o)
    {
        var slice = Ascending(o);
        if (slice.Length == 0) return null;
        return (QueueOrder.FormatLabel(o.Prefix, slice[0]), QueueOrder.FormatLabel(o.Prefix, slice[^1]), slice.Length);
    }
}
