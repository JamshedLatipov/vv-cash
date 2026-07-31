using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

/// <summary>Разбор строки поиска в поля формы регистрации. Кассир уже набрал
/// запрос — телефон или имя, — и второй раз набирать его не должен.</summary>
public class CustomerPrefillTest
{
    [Theory]
    [InlineData("7 900 123 45 67")]   // как показывает FormattedPhoneNumber
    [InlineData("+7 (900) 123-45-67")]
    [InlineData("89001234567")]        // с восьмёркой
    [InlineData("9001234567")]         // ровно десять
    public void TenOrMoreDigits_GoToPhone(string query)
    {
        var prefill = CustomerPrefill.FromSearchQuery(query);

        Assert.Equal("9001234567", prefill.PhoneNumber);
        Assert.Equal(string.Empty, prefill.FirstName);
        Assert.Equal(string.Empty, prefill.LastName);
    }

    /// <summary>Длиннее, чем любой из случаев выше: закрепляет, что берутся
    /// именно последние десять цифр (срез [^10..]), а не первые и не всё
    /// подряд. Половины строки намеренно разные — на «1234567890» дважды тест
    /// прошёл бы и при срезе [..10], то есть не проверял бы ничего.</summary>
    [Fact]
    public void LongDigitString_TakesLastTenDigits()
    {
        var prefill = CustomerPrefill.FromSearchQuery("11111111112222222222");

        Assert.Equal("2222222222", prefill.PhoneNumber);
    }

    [Fact]
    public void SingleWord_GoesToFirstName()
    {
        var prefill = CustomerPrefill.FromSearchQuery("Иван");

        Assert.Equal("Иван", prefill.FirstName);
        Assert.Equal(string.Empty, prefill.LastName);
        Assert.Equal(string.Empty, prefill.PhoneNumber);
    }

    [Fact]
    public void TwoWords_SplitIntoFirstAndLastName()
    {
        var prefill = CustomerPrefill.FromSearchQuery("Иван Петров");

        Assert.Equal("Иван", prefill.FirstName);
        Assert.Equal("Петров", prefill.LastName);
    }

    /// <summary>Отчество остаётся в фамилии, а не теряется: форма регистрации
    /// поля для отчества не имеет, и потерять введённое кассиром хуже, чем
    /// склеить.</summary>
    [Fact]
    public void ThreeWords_TailGoesToLastName()
    {
        var prefill = CustomerPrefill.FromSearchQuery("Иван Петрович Петров");

        Assert.Equal("Иван", prefill.FirstName);
        Assert.Equal("Петрович Петров", prefill.LastName);
    }

    [Fact]
    public void ExtraWhitespace_Ignored()
    {
        var prefill = CustomerPrefill.FromSearchQuery("  Иван   Петров  ");

        Assert.Equal("Иван", prefill.FirstName);
        Assert.Equal("Петров", prefill.LastName);
    }

    /// <summary>Меньше десяти цифр — это не телефон, а, например, номер карты
    /// или обрывок ввода. Уходит в имя, где кассир его увидит и поправит.</summary>
    [Fact]
    public void FewerThanTenDigits_NotTreatedAsPhone()
    {
        var prefill = CustomerPrefill.FromSearchQuery("12345");

        Assert.Equal(string.Empty, prefill.PhoneNumber);
        Assert.Equal("12345", prefill.FirstName);
    }

    /// <summary>Разделитель слов — любой пробельный символ, а не только
    /// пробел: split по null-массиву разделителей это гарантирует.</summary>
    [Fact]
    public void TabSeparatedQuery_SplitIntoFirstAndLastName()
    {
        var prefill = CustomerPrefill.FromSearchQuery("Иван\tПетров");

        Assert.Equal("Иван", prefill.FirstName);
        Assert.Equal("Петров", prefill.LastName);
    }

    /// <summary>Несколько цифр внутри слова не превращают слово в телефон —
    /// порог считается по всей строке, но здесь цифр всё равно меньше
    /// десяти, так что граница не путается со словом.</summary>
    [Fact]
    public void DigitsInsideWord_GoesToFirstName()
    {
        var prefill = CustomerPrefill.FromSearchQuery("Иван2");

        Assert.Equal("Иван2", prefill.FirstName);
        Assert.Equal(string.Empty, prefill.PhoneNumber);
    }

    /// <summary>Строка без цифр и без букв — не телефон и не пусто, поэтому
    /// уходит в имя как есть. Это текущее поведение, тест его фиксирует, а не
    /// предписывает.</summary>
    [Fact]
    public void PunctuationOnlyQuery_GoesToFirstName()
    {
        var prefill = CustomerPrefill.FromSearchQuery("---");

        Assert.Equal("---", prefill.FirstName);
        Assert.Equal(string.Empty, prefill.LastName);
        Assert.Equal(string.Empty, prefill.PhoneNumber);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyQuery_GivesEmptyPrefill(string? query)
    {
        var prefill = CustomerPrefill.FromSearchQuery(query);

        Assert.Equal(string.Empty, prefill.FirstName);
        Assert.Equal(string.Empty, prefill.LastName);
        Assert.Equal(string.Empty, prefill.PhoneNumber);
    }
}
