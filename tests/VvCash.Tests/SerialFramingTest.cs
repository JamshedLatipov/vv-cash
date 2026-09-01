using System.IO.Ports;
using System.Linq;
using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

public class SerialFramingTest
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("9N2")]
    public void Resolve_EmptyOrUnknown_Is8N1(string? id)
    {
        // 8N1 - то, что даёт голый конструктор SerialPort, то есть нынешнее поведение
        // кассы. Обновление не должно его сдвинуть.
        Assert.Same(SerialFramings.EightN1, SerialFramings.Resolve(id));
    }

    [Fact]
    public void EightN1_MatchesWhatABareSerialPortWouldUse()
    {
        Assert.Equal(8, SerialFramings.EightN1.DataBits);
        Assert.Equal(Parity.None, SerialFramings.EightN1.Parity);
        Assert.Equal(StopBits.One, SerialFramings.EightN1.StopBits);
    }

    [Fact]
    public void SevenE1_IsTheOtherOneThatShowsUpOnPoleDisplays()
    {
        Assert.Equal(7, SerialFramings.SevenE1.DataBits);
        Assert.Equal(Parity.Even, SerialFramings.SevenE1.Parity);
        Assert.Equal(StopBits.One, SerialFramings.SevenE1.StopBits);
    }

    [Fact]
    public void Catalog_HoldsTwoFramingsWithDistinctIds()
    {
        Assert.Equal(2, SerialFramings.All.Count);
        Assert.Equal(2, SerialFramings.All.Select(f => f.Id).Distinct().Count());
        Assert.Same(SerialFramings.EightN1, SerialFramings.Default);
    }
}
