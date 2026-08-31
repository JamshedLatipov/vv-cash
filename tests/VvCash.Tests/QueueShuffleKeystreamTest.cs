using System;
using System.Collections.Generic;
using System.Linq;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

/// <summary>Несущая безопасность часть перемешивания пула, поэтому у неё свои
/// тесты, отдельно от NumberPool. Детерминизм нужен для устойчивости к
/// перезапуску; отсутствие перекоса — то, ради чего этот класс вообще
/// появился взамен 32-битного сида поверх System.Random.</summary>
public class QueueShuffleKeystreamTest
{
    [Fact]
    public void SameInputsGiveTheSamePermutation()
    {
        var a = new QueueShuffleKeystream("2026-08-31", 0, "secret");
        var b = new QueueShuffleKeystream("2026-08-31", 0, "secret");

        var drawsA = Enumerable.Range(2, 50).Select(bound => a.NextIndex(bound)).ToList();
        var drawsB = Enumerable.Range(2, 50).Select(bound => b.NextIndex(bound)).ToList();

        Assert.Equal(drawsA, drawsB);
    }

    [Fact]
    public void DifferentDaysGiveDifferentPermutations()
    {
        var day1 = new QueueShuffleKeystream("2026-08-31", 0, "secret");
        var day2 = new QueueShuffleKeystream("2026-09-01", 0, "secret");

        var draws1 = Enumerable.Range(2, 50).Select(bound => day1.NextIndex(bound)).ToList();
        var draws2 = Enumerable.Range(2, 50).Select(bound => day2.NextIndex(bound)).ToList();

        Assert.NotEqual(draws1, draws2);
    }

    /// <summary>Та самая проверка, ради которой класс существует. 256 не делится
    /// на 180 поровну (256 = 180 + 76): наивный «байт % 180» отдаёт индексам
    /// 0..75 вдвое больше веса, чем 76..179 (по два байтовых значения на индекс
    /// против одного). Отбраковкой это должно сойти на нет — доля попаданий в
    /// [0, 76) должна быть около 76/180 (≈42%), а не около 152/256 (≈59%), которые
    /// дал бы модуль. Разница между этими двумя цифрами намного больше, чем шум
    /// выборки на 20000 бросков, так что порог в 10% с запасом ловит регресс к
    /// модулю и не дребезжит на настоящей случайности.</summary>
    [Fact]
    public void IndexDrawIsNotSkewedTheWayModuloWouldSkewIt()
    {
        const int bound = 180;
        const int lowBand = 76; // 256 % bound
        const int samples = 20000;

        var keystream = new QueueShuffleKeystream("2026-08-31", 0, "secret");
        var lowCount = 0;
        for (var i = 0; i < samples; i++)
        {
            if (keystream.NextIndex(bound) < lowBand) lowCount++;
        }

        var expected = samples * (lowBand / (double)bound);
        Assert.InRange(lowCount, expected * 0.9, expected * 1.1);
    }

    [Fact]
    public void SingleElementBoundAlwaysDrawsZero()
    {
        var keystream = new QueueShuffleKeystream("2026-08-31", 0, "secret");

        for (var i = 0; i < 10; i++)
            Assert.Equal(0, keystream.NextIndex(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(257)]
    public void OutOfRangeBoundIsRejected(int bound)
    {
        var keystream = new QueueShuffleKeystream("2026-08-31", 0, "secret");

        Assert.Throws<ArgumentOutOfRangeException>(() => keystream.NextIndex(bound));
    }

    /// <summary>Fisher–Yates поверх этого потока — то, что реально видит
    /// NumberPool, — должен вернуть допустимую перестановку: то же множество
    /// элементов, просто в другом порядке. Ловит ошибку на уровне интеграции
    /// (например, границы Fisher–Yates), которую тесты только по NextIndex не
    /// увидят.</summary>
    [Fact]
    public void DrivesAValidFisherYatesPermutation()
    {
        var keystream = new QueueShuffleKeystream("2026-08-31", 0, "secret");
        var slice = Enumerable.Range(100, 180).ToArray();
        var original = (int[])slice.Clone();

        for (var i = slice.Length - 1; i > 0; i--)
        {
            var j = keystream.NextIndex(i + 1);
            (slice[i], slice[j]) = (slice[j], slice[i]);
        }

        Assert.Equal(original.OrderBy(n => n), slice.OrderBy(n => n));
        Assert.NotEqual(original, slice);
    }
}
