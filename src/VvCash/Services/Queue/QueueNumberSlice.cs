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
/// обновились в разное время одного дня. Без перемешивания — сплошные блоки,
/// поделённые поровну (count / TillCount, первым остатку — по одному лишнему),
/// а не ceil(count / TillCount): ceil на 100–120 (21 номер) на 9 касс отдаёт
/// по 3 первым семи кассам и ничего — последним двум, а предпросмотр в
/// настройках показывает только срез своей кассы, так что никто не заметит
/// пустую до жалобы с точки. Ровное деление гарантирует, что при count ≥
/// TillCount каждая касса получит хотя бы один номер.</summary>
public static class QueueNumberSlice
{
    /// <summary>Номера этой кассы по возрастанию. Пусто, если срез пуст
    /// (касса за концом короткого диапазона) или настройки вырождены — не
    /// бросает для значений в пределах клэмпов QueueNumberOptions: настройки
    /// нормализует SettingsService, а тесты и предпросмотр могут собрать
    /// record напрямую.</summary>
    public static int[] Ascending(QueueNumberOptions options)
    {
        var count = options.Max - options.Min + 1;
        if (count <= 0 || options.TillCount < 1 || options.TillIndex < 0 || options.TillIndex >= options.TillCount)
            return Array.Empty<int>();

        if (options.Shuffle)
        {
            return Enumerable.Range(options.Min, count)
                .Where(n => (n - options.Min) % options.TillCount == options.TillIndex)
                .ToArray();
        }

        var baseSize = count / options.TillCount;
        var remainder = count % options.TillCount;
        var start = options.Min + options.TillIndex * baseSize + Math.Min(options.TillIndex, remainder);
        var size = baseSize + (options.TillIndex < remainder ? 1 : 0);
        return Enumerable.Range(start, size).ToArray();
    }

    /// <summary>Первый и последний номер среза с префиксом и их число — для
    /// строки предпросмотра в настройках. Null для пустого среза.</summary>
    public static (string First, string Last, int Count)? Summarize(QueueNumberOptions options)
    {
        var slice = Ascending(options);
        if (slice.Length == 0) return null;
        return (QueueOrder.FormatLabel(options.Prefix, slice[0]), QueueOrder.FormatLabel(options.Prefix, slice[^1]), slice.Length);
    }
}
