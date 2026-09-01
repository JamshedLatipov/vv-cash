using System;
using System.Collections.Generic;

namespace VvCash.Services.Hardware;

/// <summary>Одна комбинация автоподбора и её номер — то самое число, которое кассир
/// читает на табло.</summary>
public sealed record DisplayProbe(int Number, IDisplayProtocol Protocol, int BaudRate);

/// <summary>Что перебирает автоподбор и в каком порядке.
///
/// Чистая функция, вынесенная отдельно от экрана настроек по той же причине, что
/// CustomerDisplayPlacementSelector: решение, зависящее только от каталогов, должно
/// проверяться без Avalonia и без COM-порта.
///
/// Формат кадра и DTR/RTS сюда не входят намеренно. Они редкие, а в кресте с ними
/// перебор вырос бы с 28 шагов до 112 — почти три минуты, столько кассир за табло не
/// отследит. Не нашлось — ставятся руками, и перебор гоняется ещё раз.</summary>
public static class DisplayProbePlan
{
    /// <summary>Низ списка включён по следу живого разбора: встречалось табло, которое
    /// гасло на всём выше 2400. Перебор, начинающийся с 9600, такое не находит.</summary>
    public static IReadOnlyList<int> BaudRates { get; } =
        Array.AsReadOnly(new[] { 600, 1200, 2400, 4800, 9600, 19200, 38400 });

    private static readonly IReadOnlyList<DisplayProbe> Plan = BuildPlan();

    /// <summary>Протокол снаружи, скорость внутри: соседние номера отличаются только
    /// скоростью, и кассиру, который видит на табло два читаемых числа подряд, сразу
    /// понятно, что дело в ней, а не в диалекте.</summary>
    private static IReadOnlyList<DisplayProbe> BuildPlan()
    {
        var probes = new List<DisplayProbe>();
        var number = 1;

        foreach (var protocol in DisplayProtocols.All)
        {
            foreach (var baud in BaudRates)
            {
                probes.Add(new DisplayProbe(number, protocol, baud));
                number++;
            }
        }

        return probes.AsReadOnly();
    }

    public static IReadOnlyList<DisplayProbe> Build() => Plan;

    /// <summary>Комбинация по номеру, или null, если такого номера нет. Экран
    /// настроек отличает «кассир ошибся при вводе» от «номер есть» только по этому
    /// null.</summary>
    public static DisplayProbe? Find(int number)
    {
        foreach (var probe in Plan)
        {
            if (probe.Number == number) return probe;
        }

        return null;
    }
}
