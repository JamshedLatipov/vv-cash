using System.Linq;
using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

/// <summary>Маска телефона и каталог форматов. Всё здесь чистое — ни Avalonia,
/// ни настроек, ни сети.</summary>
public class PhoneFormatTest
{
    [Theory]
    [InlineData("RU", 10)]
    [InlineData("TJ", 9)]
    [InlineData("UZ", 9)]
    public void DigitCount_CountsPlaceholdersInMask(string id, int expected)
    {
        var format = PhoneFormats.Resolve(id);

        Assert.Equal(expected, format.DigitCount);
        Assert.Equal(expected, format.Mask.Count(c => c == '#'));
    }

    [Fact]
    public void Format_OnEmptyInput_EqualsPlaceholder()
    {
        var format = PhoneFormats.Resolve("TJ");

        Assert.Equal(format.Placeholder, format.Format(string.Empty));
        Assert.Equal("+992 (__) ___-__-__", format.Placeholder);
    }

    [Fact]
    public void Format_OnNull_EqualsPlaceholder()
    {
        var format = PhoneFormats.Resolve("RU");

        Assert.Equal(format.Placeholder, format.Format(null));
    }

    /// <summary>Цифры слева направо, литералы маски на местах, хвост —
    /// подчёркивания: кассир видит, сколько ещё набирать.</summary>
    [Fact]
    public void Format_OnPartialInput_FillsLeftToRight()
    {
        var format = PhoneFormats.Resolve("TJ");

        Assert.Equal("+992 (90) 12_-__-__", format.Format("9012"));
    }

    [Fact]
    public void Format_OnFullInput_LeavesNoPlaceholders()
    {
        var format = PhoneFormats.Resolve("RU");

        var result = format.Format("9001234567");

        Assert.Equal("+7 (900) 123-45-67", result);
        Assert.DoesNotContain('_', result);
    }

    /// <summary>Нумпад и так не даёт набрать лишнее, но формат не должен от
    /// этого зависеть: строка приходит и из префилла поиска.</summary>
    [Fact]
    public void Format_IgnoresDigitsBeyondTheMask()
    {
        var format = PhoneFormats.Resolve("TJ");

        Assert.Equal("+992 (90) 123-45-67", format.Format("901234567999"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("XX")]
    public void Resolve_OnUnusableId_FallsBackToRu(string? id)
    {
        Assert.Same(PhoneFormats.Default, PhoneFormats.Resolve(id));
        Assert.Equal("RU", PhoneFormats.Resolve(id).Id);
    }

    /// <summary>Регистр не должен решать судьбу настройки, отредактированной
    /// руками в файле.</summary>
    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        Assert.Equal("TJ", PhoneFormats.Resolve("tj").Id);
    }

    [Fact]
    public void Catalogue_HasUniqueIdsAndNoBlankFields()
    {
        Assert.Equal(3, PhoneFormats.All.Count);
        Assert.Equal(PhoneFormats.All.Count, PhoneFormats.All.Select(f => f.Id).Distinct().Count());

        foreach (var f in PhoneFormats.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Id));
            Assert.False(string.IsNullOrWhiteSpace(f.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(f.CountryCode));
            Assert.True(f.DigitCount > 0);
        }
    }

    [Fact]
    public void Default_IsPartOfTheCatalogue()
    {
        Assert.Contains(PhoneFormats.Default, PhoneFormats.All);
    }
}
