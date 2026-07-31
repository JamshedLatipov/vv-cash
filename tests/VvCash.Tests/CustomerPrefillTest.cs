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
