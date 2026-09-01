using System;
using System.Collections.Generic;
using VvCash.Models;

namespace VvCash.Services.Hardware;

/// <summary>Одна комбинация автоподбора и её номер — то самое число, которое кассир
/// читает на табло или выбирает из списка после «Стоп».</summary>
public sealed record DisplayProbe(
    int Number,
    string PortName,
    IDisplayProtocol Protocol,
    int BaudRate,
    SerialFraming Framing,
    bool DtrRts);

/// <summary>Что перебирает автоподбор и в каком порядке.
///
/// Чистая функция, вынесенная отдельно от экрана настроек по той же причине, что
/// CustomerDisplayPlacementSelector: решение, зависящее только от каталогов, должно
/// проверяться без Avalonia и без COM-порта.
///
/// Перебираются все пять осей — порт, скорость, формат кадра, DTR/RTS и диалект.
/// Раньше две из них были вынесены в ручные поля ради короткого прогона, но живой
/// разбор показал, что угадать их с экрана нечем: касса не может отличить «порт
/// принял байты» от «табло их поняло», а материнский UART принимает всё и всегда.
/// Полный крест длиннее, зато не оставляет угла, куда можно не заглянуть.</summary>
public static class DisplayProbePlan
{
    /// <summary>Порядок не косметика: кассир смотрит на табло вживую, и чем раньше
    /// встретится рабочая скорость, тем меньше шансов, что он устанет и бросит.
    /// 9600 и 2400 — самые частые на этих панелях, поэтому идут первыми.
    ///
    /// Низ списка при этом обязан остаться. Встречалось табло, которое гасло на всём
    /// выше 2400: перебор, начинающийся с 9600 и не доходящий до 600, такое находит
    /// только случайно.</summary>
    public static IReadOnlyList<int> BaudRates { get; } =
        Array.AsReadOnly(new[] { 9600, 2400, 19200, 38400, 4800, 1200, 600 });

    /// <summary>Строит план для этих портов.
    ///
    /// Порядок вложенности выбран под то, как это читается глазами: диалект меняется
    /// быстрее всего, поэтому четыре подряд идущих шага делят порт, скорость и формат
    /// кадра. Если транспорт угадан, оживёт хотя бы один из четырёх — кассир видит
    /// вспышку подряд, а не одиночную, которую легко проморгать. Вынеси протокол
    /// наружу — и верные комбинации размажутся по всему прогону поодиночке.</summary>
    public static IReadOnlyList<DisplayProbe> Build(IReadOnlyList<string> ports)
    {
        var probes = new List<DisplayProbe>();
        var number = 1;

        foreach (var port in ports)
        {
            foreach (var baud in BaudRates)
            {
                foreach (var framing in SerialFramings.All)
                {
                    foreach (var dtrRts in new[] { true, false })
                    {
                        foreach (var protocol in DisplayProtocols.All)
                        {
                            probes.Add(new DisplayProbe(number, port, protocol, baud, framing, dtrRts));
                            number++;
                        }
                    }
                }
            }
        }

        return probes.AsReadOnly();
    }

    /// <summary>Комбинация по номеру, или null, если такого номера нет. Экран настроек
    /// отличает «кассир ошибся при вводе» от «номер есть» только по этому null.</summary>
    public static DisplayProbe? Find(IReadOnlyList<DisplayProbe> plan, int number)
    {
        foreach (var probe in plan)
        {
            if (probe.Number == number) return probe;
        }

        return null;
    }

    /// <summary>Строка для экрана: все пять осей словами.
    ///
    /// Перечислено всё до единой оси намеренно. По этой же строке кассир ставит
    /// настройки руками, если применение по номеру почему-то не подошло, — пропущенная
    /// ось означает найденную комбинацию, которую нельзя повторить.</summary>
    public static string Describe(DisplayProbe probe)
        => $"{probe.PortName} {probe.BaudRate} {probe.Framing.Id} " +
           $"DTR{(probe.DtrRts ? "+" : "-")} {probe.Protocol.DisplayName}";
}
