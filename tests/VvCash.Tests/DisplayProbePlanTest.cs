using System.Linq;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

public class DisplayProbePlanTest
{
    [Fact]
    public void Plan_IsEveryProtocolAtEveryBaudRate()
    {
        var plan = DisplayProbePlan.Build();

        Assert.Equal(DisplayProtocols.All.Count * DisplayProbePlan.BaudRates.Count, plan.Count);
        Assert.Equal(28, plan.Count);
    }

    [Fact]
    public void Plan_NumbersRunFromOneWithoutGaps()
    {
        // Номер - это то, что кассир прочитал на табло и записал на бумажке. Дырка в
        // нумерации означала бы номер, который ввести можно, а применить нельзя.
        var plan = DisplayProbePlan.Build();

        Assert.Equal(Enumerable.Range(1, plan.Count), plan.Select(p => p.Number));
    }

    [Fact]
    public void Plan_OrderIsFixed()
    {
        // Порядок закреплён нарочно. Номер, увиденный на табло, обязан значить одно и
        // то же между запусками и между версиями кассы - иначе записанное кассиром
        // число превращается в мусор при первом же обновлении.
        var plan = DisplayProbePlan.Build();

        Assert.Same(DisplayProtocols.EscPos, plan[0].Protocol);
        Assert.Equal(600, plan[0].BaudRate);

        Assert.Same(DisplayProtocols.EscPos, plan[6].Protocol);
        Assert.Equal(38400, plan[6].BaudRate);

        Assert.Same(DisplayProtocols.Cd5220, plan[7].Protocol);
        Assert.Equal(600, plan[7].BaudRate);

        Assert.Same(DisplayProtocols.Raw, plan[27].Protocol);
        Assert.Equal(38400, plan[27].BaudRate);
    }

    [Fact]
    public void BaudRates_ReachBelow2400()
    {
        // Низ включён по следу живого разбора: табло гасло на всём выше 2400, и
        // перебор, начинающийся с 9600, такое не нашёл бы вовсе.
        Assert.Contains(600, DisplayProbePlan.BaudRates);
        Assert.Contains(1200, DisplayProbePlan.BaudRates);
    }

    [Fact]
    public void Find_UnknownNumber_IsNull()
    {
        Assert.Null(DisplayProbePlan.Find(0));
        Assert.Null(DisplayProbePlan.Find(29));
    }

    [Fact]
    public void Find_KnownNumber_ReturnsThatCombination()
    {
        var probe = DisplayProbePlan.Find(8);

        Assert.NotNull(probe);
        Assert.Same(DisplayProtocols.Cd5220, probe!.Protocol);
        Assert.Equal(600, probe.BaudRate);
    }
}
