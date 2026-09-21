using System.Linq;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

public class QueueNumberSliceTest
{
    private static QueueNumberOptions Options(
        int tillIndex = 0, int tillCount = 5, int min = 100, int max = 999, bool shuffle = true)
        => new(tillIndex, tillCount, min, max, shuffle, "", "s");

    /// <summary>Регрессия: при настройках по умолчанию срез обязан совпасть с
    /// прежней константной формулой n % 5 == TillIndex — иначе кассы,
    /// обновившиеся в разное время одного дня, столкнутся.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void TheDefaultShapeIsTheLegacyModuloSlice(int tillIndex)
    {
        var slice = QueueNumberSlice.Ascending(Options(tillIndex));

        var legacy = Enumerable.Range(100, 900).Where(n => n % 5 == tillIndex).ToArray();
        Assert.Equal(legacy, slice);
        Assert.Equal(180, slice.Length);
    }

    /// <summary>Одинаковый диапазон делится без пропусков и без пересечений
    /// у обоих правил, даже когда он не делится на TillCount без остатка
    /// (56 на 3 кассы). При отсутствии шаффла дополнительно проверяем, что
    /// каждый срез — сплошной блок, а не что попало: это то, что видит
    /// клиент на бумаге.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SlicesOfAllTillsPartitionTheRange(bool shuffle)
    {
        var slices = Enumerable.Range(0, 3)
            .Select(i => QueueNumberSlice.Ascending(Options(i, tillCount: 3, min: 7, max: 62, shuffle: shuffle)))
            .ToArray();

        var all = slices.SelectMany(s => s).OrderBy(n => n).ToArray();
        Assert.Equal(Enumerable.Range(7, 56).ToArray(), all);

        if (!shuffle)
        {
            foreach (var slice in slices)
            {
                Assert.Equal(slice.Length, slice[^1] - slice[0] + 1);
            }
        }
    }

    [Fact]
    public void SequentialSlicesAreContiguousBlocks()
    {
        var first = QueueNumberSlice.Ascending(Options(0, tillCount: 2, min: 1, max: 99, shuffle: false));
        var second = QueueNumberSlice.Ascending(Options(1, tillCount: 2, min: 1, max: 99, shuffle: false));

        Assert.Equal(Enumerable.Range(1, 50).ToArray(), first);
        Assert.Equal(Enumerable.Range(51, 49).ToArray(), second);
    }

    /// <summary>Регрессия к ceil(count / TillCount): на 100–120 (21 номер) на
    /// 9 касс ceil отдал бы по 3 первым семи и ничего последним двум, а
    /// предпросмотр показывает только свою кассу — никто бы не заметил.
    /// Ровное деление обязано дать каждой кассе хотя бы один номер.</summary>
    [Fact]
    public void EveryTillGetsANumberWhileTheRangeIsLongEnough()
    {
        var sizes = Enumerable.Range(0, 9)
            .Select(i => QueueNumberSlice.Ascending(Options(i, tillCount: 9, min: 100, max: 120, shuffle: false)).Length)
            .ToArray();

        Assert.All(sizes, size => Assert.True(size > 0));
        Assert.Equal(new[] { 3, 3, 3, 2, 2, 2, 2, 2, 2 }, sizes);
    }

    [Fact]
    public void ATillIndexBeyondTheTillCountGetsAnEmptySlice()
    {
        Assert.Empty(QueueNumberSlice.Ascending(Options(5, tillCount: 5)));
        Assert.Empty(QueueNumberSlice.Ascending(Options(5, tillCount: 5, shuffle: false)));
    }

    [Fact]
    public void ASingleTillOwnsTheWholeRange()
    {
        Assert.Equal(Enumerable.Range(1, 99).ToArray(),
            QueueNumberSlice.Ascending(Options(0, tillCount: 1, min: 1, max: 99)));
        Assert.Equal(Enumerable.Range(1, 99).ToArray(),
            QueueNumberSlice.Ascending(Options(0, tillCount: 1, min: 1, max: 99, shuffle: false)));
    }

    [Fact]
    public void ATillPastTheRangeGetsAnEmptySlice()
    {
        Assert.Empty(QueueNumberSlice.Ascending(Options(4, tillCount: 5, min: 1, max: 3)));
        Assert.Empty(QueueNumberSlice.Ascending(Options(4, tillCount: 5, min: 1, max: 3, shuffle: false)));
    }

    [Fact]
    public void ADegenerateRangeOrTillCountYieldsNothingRatherThanThrowing()
    {
        Assert.Empty(QueueNumberSlice.Ascending(Options(0, min: 500, max: 100)));
        Assert.Empty(QueueNumberSlice.Ascending(Options(0, tillCount: 0)));
    }

    [Fact]
    public void TheSummaryNamesTheEndsWithThePrefixAndCounts()
    {
        var options = new QueueNumberOptions(1, 2, 1, 99, false, "A-", "s");

        var summary = QueueNumberSlice.Summarize(options);

        Assert.NotNull(summary);
        Assert.Equal("A-51", summary!.Value.First);
        Assert.Equal("A-99", summary.Value.Last);
        Assert.Equal(49, summary.Value.Count);
    }

    [Fact]
    public void TheSummaryOfAnEmptySliceIsNull()
    {
        Assert.Null(QueueNumberSlice.Summarize(Options(4, tillCount: 5, min: 1, max: 3)));
    }
}
