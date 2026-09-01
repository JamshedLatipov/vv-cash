using System.Linq;
using VvCash.Models;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

public class DisplayProbePlanTest
{
    private static readonly string[] TwoPorts = { "COM1", "COM2" };

    [Fact]
    public void Plan_IsEveryCombinationOfEveryAxis()
    {
        var plan = DisplayProbePlan.Build(TwoPorts);

        var expected = TwoPorts.Length
            * DisplayProbePlan.BaudRates.Count
            * SerialFramings.All.Count
            * 2                                   // DTR/RTS включён и выключен
            * DisplayProtocols.All.Count;

        Assert.Equal(expected, plan.Count);
        Assert.Equal(224, plan.Count);
    }

    [Fact]
    public void Plan_NumbersRunFromOneWithoutGaps()
    {
        // Номер — это то, что кассир прочитал на табло или выбрал из списка после
        // «Стоп». Дырка в нумерации означала бы номер, который ввести можно, а
        // применить нельзя.
        var plan = DisplayProbePlan.Build(TwoPorts);

        Assert.Equal(Enumerable.Range(1, plan.Count), plan.Select(p => p.Number));
    }

    [Fact]
    public void Plan_KeepsTheProtocolInnermost()
    {
        // Четыре подряд идущих шага обязаны делить порт, скорость и формат кадра,
        // меняя только диалект. Если транспорт угадан, оживёт хотя бы один из
        // четырёх — кассир видит заметную вспышку подряд, а не одиночную, которую
        // легко проморгать. Перестрой порядок так, чтобы протокол шёл снаружи, и
        // верные комбинации размажутся по всему прогону поодиночке.
        var plan = DisplayProbePlan.Build(TwoPorts);
        var first = plan.Take(DisplayProtocols.All.Count).ToList();

        Assert.All(first, p => Assert.Equal(first[0].PortName, p.PortName));
        Assert.All(first, p => Assert.Equal(first[0].BaudRate, p.BaudRate));
        Assert.All(first, p => Assert.Same(first[0].Framing, p.Framing));
        Assert.All(first, p => Assert.Equal(first[0].DtrRts, p.DtrRts));
        Assert.Equal(DisplayProtocols.All.Count, first.Select(p => p.Protocol).Distinct().Count());
    }

    [Fact]
    public void BaudRates_PutTheLikelyOnesFirstAndStillReachBelow2400()
    {
        // Порядок скоростей — не косметика: кассир смотрит на табло вживую, и чем
        // раньше встретится рабочая, тем меньше шансов, что он устанет и бросит.
        // 9600 и 2400 самые частые на этих панелях. Низ списка при этом обязан
        // остаться: встречалось табло, которое гасло на всём выше 2400.
        Assert.Equal(9600, DisplayProbePlan.BaudRates[0]);
        Assert.Equal(2400, DisplayProbePlan.BaudRates[1]);
        Assert.Contains(1200, DisplayProbePlan.BaudRates);
        Assert.Contains(600, DisplayProbePlan.BaudRates);
    }

    [Fact]
    public void Plan_CoversBothFramingsAndBothControlLineStates()
    {
        var plan = DisplayProbePlan.Build(TwoPorts);

        Assert.Contains(plan, p => p.Framing.Id == "8N1" && p.DtrRts);
        Assert.Contains(plan, p => p.Framing.Id == "8N1" && !p.DtrRts);
        Assert.Contains(plan, p => p.Framing.Id == "7E1" && p.DtrRts);
        Assert.Contains(plan, p => p.Framing.Id == "7E1" && !p.DtrRts);
    }

    [Fact]
    public void Plan_WithNoPorts_IsEmptyRatherThanThrowing()
    {
        // Касса без COM-портов — не отказ, а нормальное состояние; экран настроек
        // отличает «перебирать нечего» от «перебор не нашёл» только по пустому плану.
        Assert.Empty(DisplayProbePlan.Build(new string[0]));
    }

    [Fact]
    public void Find_UnknownNumber_IsNull()
    {
        var plan = DisplayProbePlan.Build(TwoPorts);

        Assert.Null(DisplayProbePlan.Find(plan, 0));
        Assert.Null(DisplayProbePlan.Find(plan, plan.Count + 1));
    }

    [Fact]
    public void Find_KnownNumber_ReturnsThatCombination()
    {
        var plan = DisplayProbePlan.Build(TwoPorts);
        var probe = DisplayProbePlan.Find(plan, 5);

        Assert.NotNull(probe);
        Assert.Equal(5, probe!.Number);
        Assert.Same(plan[4], probe);
    }

    [Fact]
    public void Describe_NamesEveryAxisSoTheStatusLineIsSelfExplanatory()
    {
        // Кассир видит эту строку и по ней же потом ставит настройки руками, если
        // применение по номеру почему-то не подошло. Пропущенная ось означает, что
        // найденную комбинацию нельзя повторить.
        var probe = DisplayProbePlan.Build(new[] { "COM2" })[0];
        var text = DisplayProbePlan.Describe(probe);

        Assert.Contains("COM2", text);
        Assert.Contains("9600", text);
        Assert.Contains(probe.Framing.Id, text);
        Assert.Contains(probe.Protocol.DisplayName, text);
        Assert.Contains("DTR", text);
    }
}
