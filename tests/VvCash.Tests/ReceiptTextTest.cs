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

    [Fact]
    public void PadLine_OverflowsPastTheWidth_WhenTheSidesExactlyFillIt()
    {
        // At left.Length + right.Length == width there is no room left for the
        // mandatory separating space, so the line is deliberately width + 1
        // long: one character wraps onto its own line on the printer. Sticking
        // the name and price together with no gap is worse than that wrap, so
        // this is the chosen trade-off, not an oversight — a TS twin reaching
        // for padEnd(width) would not reproduce it.
        var left = new string('x', 30);
        var right = new string('y', 2);

        var line = ReceiptText.PadLine(left, right, 32);

        Assert.Equal(left + " " + right, line);
        Assert.Equal(33, line.Length);
    }

    [Fact]
    public void Money_RoundsAwayFromZero_UnlikeBankersRoundingOrJsToFixed()
    {
        // .NET's own Math.Round(2.005m, 2) would give 2.00 here (banker's
        // rounding), and JavaScript's toFixed(2) rounds by the binary float
        // representation and also lands on 2.00 for both values below. This
        // method rounds away from zero instead, so a real sale of
        // 13.50 x 0.150 prints 2.03 on the till. The TS twin must replicate
        // away-from-zero rounding explicitly — toFixed is not a substitute.
        Assert.Equal("2.01", ReceiptText.Money(2.005m));
        Assert.Equal("2.68", ReceiptText.Money(2.675m));
    }
}
