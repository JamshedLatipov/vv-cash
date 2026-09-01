using System.Globalization;
using System.Threading;
using VvCash.Services.Rendering;
using Xunit;

namespace VvCash.Tests;

public class ReceiptTextTest
{
    [Fact]
    public void Money_UsesADotOnEveryRegister_WhateverTheSystemLocale()
    {
        // Интерполяция с ":F2" брала разделитель из локали ОС, и одна и та же
        // продажа печаталась 20.00 на одной кассе и 20,00 на соседней.
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            Assert.Equal("20.00", ReceiptText.Money(20m));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void PadLine_PushesTheRightSideToTheGivenWidth()
    {
        Assert.Equal("A" + new string(' ', 30) + "B", ReceiptText.PadLine("A", "B", 32));
    }

    [Fact]
    public void PadLine_KeepsAtLeastOneSpace_WhenTheSidesDoNotFit()
    {
        // Слипшиеся название и цена нечитаемы; переполнение по ширине принтер
        // перенесёт сам.
        var line = ReceiptText.PadLine(new string('x', 30), new string('y', 30), 32);

        Assert.Equal(new string('x', 30) + " " + new string('y', 30), line);
    }

    [Fact]
    public void Truncate_ClipsToTheWidth()
    {
        Assert.Equal("abc", ReceiptText.Truncate("abcdef", 3));
        Assert.Equal("abc", ReceiptText.Truncate("abc", 32));
    }
}
