using System.Linq;
using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

/// <summary>Каталог кодовых страниц. Всё чистое — ни принтера, ни настроек.</summary>
public class EscPosCodePageTest
{
    [Theory]
    [InlineData("CP866", 866, 17)]
    [InlineData("CP1251", 1251, 46)]
    [InlineData("PC437", 437, 0)]
    public void Resolve_ReturnsTheCatalogEntry(string id, int codePage, byte selector)
    {
        var entry = EscPosCodePages.Resolve(id);

        Assert.Equal(codePage, entry.CodePage);
        Assert.Equal(selector, entry.EscTSelector);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CP-does-not-exist")]
    public void Resolve_OnEmptyOrUnknown_IsTheDefault(string? id)
    {
        // Правило «пусто или незнакомо — значит CP866» должно быть одно и
        // проверяться без файловой системы, ровно как у PhoneFormats.
        Assert.Same(EscPosCodePages.Default, EscPosCodePages.Resolve(id));
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        Assert.Same(EscPosCodePages.Cp1251, EscPosCodePages.Resolve("cp1251"));
    }

    [Fact]
    public void Catalog_MakesSingleByteEncodingsAvailable()
    {
        // .NET Core не несёт однобайтовых кодировок: без RegisterProvider это
        // NotSupportedException. Program.Main в тестовом процессе не выполняется,
        // поэтому регистрация обязана жить там, куда ходят за кодировкой.
        Assert.Equal(866, EscPosCodePages.Cp866.Encoding.CodePage);
        Assert.Equal(1251, EscPosCodePages.Cp1251.Encoding.CodePage);
    }

    [Fact]
    public void Encoding_RoundTripsRussian()
    {
        var bytes = EscPosCodePages.Cp866.Encoding.GetBytes("Плитка");

        Assert.Equal(6, bytes.Length); // однобайтовая: буква = байт
        Assert.Equal("Плитка", EscPosCodePages.Cp866.Encoding.GetString(bytes));
    }

    [Fact]
    public void Encoding_ReplacesAnUncoveredLetterWithQuestionMark()
    {
        // Таджикской ӯ нет ни в CP866, ни в CP1251, и однобайтовой таблицы под
        // таджикский у ESC/POS нет вообще. Замена названа явно, чтобы её было
        // на что предъявить в пробной печати, а не обнаруживать на товарах.
        var bytes = EscPosCodePages.Cp866.Encoding.GetBytes("ӯ");

        Assert.Equal(new byte[] { (byte)'?' }, bytes);
    }

    [Fact]
    public void All_ContainsEveryDeclaredEntry()
    {
        Assert.Equal(3, EscPosCodePages.All.Count);
        Assert.Contains(EscPosCodePages.Cp866, EscPosCodePages.All);
        Assert.Contains(EscPosCodePages.Cp1251, EscPosCodePages.All);
        Assert.Contains(EscPosCodePages.Pc437, EscPosCodePages.All);
        Assert.All(EscPosCodePages.All, e => Assert.False(string.IsNullOrWhiteSpace(e.DisplayName)));
    }
}
