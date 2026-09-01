using System.Collections.Generic;
using System.Linq;
using System.Text;
using VvCash.Models;
using VvCash.Services.Hardware;
using VvCash.Services.Hardware.Protocols;
using Xunit;

namespace VvCash.Tests;

public class DisplayProtocolTest
{
    /// <summary>Байт в байт то, что уходило до появления протоколов: ESC @, ESC t n,
    /// затем 40 символов двумя колонками по 20. Это единственная реализация, про
    /// которую известно, что она работает на живом железе в магазинах, и переезд в
    /// отдельный класс не имеет права её сдвинуть. Ожидание собрано здесь вручную, а
    /// не вызовом того же кода — иначе тест подтверждал бы сам себя.</summary>
    [Fact]
    public void EscPos_ItemFrame_IsByteIdenticalToTheShippedFormat()
    {
        var protocol = new EscPosDisplayProtocol();
        var cp = EscPosCodePages.Cp866;

        var actual = protocol.BuildItem("Молоко", 50m, cp);

        var expected = new List<byte> { 0x1B, 0x40, 0x1B, 0x74, cp.EscTSelector };
        expected.AddRange(cp.Encoding.GetBytes("Молоко".PadRight(20) + "50.00".PadRight(20)));

        Assert.Equal(expected.ToArray(), actual);
    }

    [Fact]
    public void EscPos_TotalFrame_SaysTotalAndCarriesNoCurrency()
    {
        var protocol = new EscPosDisplayProtocol();
        var text = EscPosDisplayProtocol.BuildTotalFrame(100m);

        Assert.Equal(40, text.Length);
        Assert.StartsWith("TOTAL", text);
        Assert.Contains("100.00", text);
        Assert.DoesNotContain("$", text);
    }

    /// <summary>Пробник обязан читаться и тогда, когда кодовая страница выбрана
    /// неверно — иначе он проверял бы заодно и её, то есть отвечал бы сразу на два
    /// вопроса и ни на один внятно. Поэтому в нём нет ни ESC t, ни байтов старше
    /// 0x7F.</summary>
    [Fact]
    public void EscPos_Probe_IsPlainAsciiAndSelectsNoCodePage()
    {
        var bytes = new EscPosDisplayProtocol().BuildProbe(17);

        Assert.DoesNotContain((byte)0x74, bytes);          // ESC t не отправлялся
        Assert.All(bytes, b => Assert.True(b <= 0x7F));
        Assert.Contains("17", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Cd5220_WritesEachLineWithItsOwnCommandAndTerminator()
    {
        // ESC Q A - верхняя строка, ESC Q B - нижняя, каждая закрывается CR. Строки
        // добиваются пробелами до 20: часть клонов не гасит остаток строки по CR
        // сама, и без набивки на табло оставался бы хвост предыдущего товара.
        var protocol = new Cd5220DisplayProtocol();
        var cp = EscPosCodePages.Cp866;

        var actual = protocol.BuildItem("Молоко", 50m, cp);

        var expected = new List<byte> { 0x1B, 0x51, 0x41 };
        expected.AddRange(cp.Encoding.GetBytes("Молоко".PadRight(20)));
        expected.Add(0x0D);
        expected.AddRange(new byte[] { 0x1B, 0x51, 0x42 });
        expected.AddRange(cp.Encoding.GetBytes("50.00".PadRight(20)));
        expected.Add(0x0D);

        Assert.Equal(expected.ToArray(), actual);
    }

    [Fact]
    public void Cd5220_Probe_IsPlainAscii()
    {
        var bytes = new Cd5220DisplayProtocol().BuildProbe(17);

        Assert.All(bytes, b => Assert.True(b <= 0x7F));
        Assert.Contains("17", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Numeric_DropsTheItemNameAndShowsTheTotal()
    {
        // Сегментное табло букв не умеет физически. Название отбрасывается здесь, а
        // не в PosViewModel: тот шлёт один и тот же кадр всем табло и о разнице
        // между ними не знает.
        var bytes = new NumericDisplayProtocol().BuildItem("Молоко", 50m, EscPosCodePages.Cp866);

        Assert.Equal("50.00\r", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Numeric_LineWithNoDigits_Clears()
    {
        // ShowLineAsync("Thank you!", "Come again!") после оплаты. Цифр в нём нет,
        // показывать нечего - табло уходит в ноль, а не остаётся с суммой прошлого
        // покупателя.
        var bytes = new NumericDisplayProtocol().BuildLine("Thank you!", "Come again!", EscPosCodePages.Cp866);

        Assert.Equal("0.00\r", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Numeric_LineWithDigits_ShowsThem()
    {
        // Кнопка проверки шлёт "TEST 8888.88" второй строкой именно ради этой ветки:
        // на сегментном табло 8888.88 зажигает все сегменты - классическая
        // самопроверка панели, которую ни с покоем, ни с суммой не спутать.
        var bytes = new NumericDisplayProtocol().BuildLine("VV CASH", "TEST 8888.88", EscPosCodePages.Cp866);

        Assert.Equal("8888.88\r", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Numeric_Probe_IsPlainAscii()
    {
        var bytes = new NumericDisplayProtocol().BuildProbe(17);

        Assert.All(bytes, b => Assert.True(b <= 0x7F));
        Assert.Contains("17", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Raw_SendsFortyCharactersAndNotOneCommandByte()
    {
        // Для табло, которые принимают текст как есть. Ни одного управляющего байта:
        // именно этим RAW и отличается от ESCPOS, и именно это надо сторожить -
        // добавь сюда ESC @ "на всякий случай", и протокол перестанет быть собой.
        var cp = EscPosCodePages.Cp866;
        var actual = new RawDisplayProtocol().BuildItem("Молоко", 50m, cp);

        Assert.Equal(cp.Encoding.GetBytes("Молоко".PadRight(20) + "50.00".PadRight(20)), actual);
        Assert.All(actual, b => Assert.True(b >= 0x20));
    }

    [Fact]
    public void Raw_Probe_IsPlainAscii()
    {
        var bytes = new RawDisplayProtocol().BuildProbe(17);

        Assert.All(bytes, b => Assert.True(b <= 0x7F));
        Assert.Contains("17", Encoding.ASCII.GetString(bytes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AEDEX")]
    public void Resolve_EmptyOrUnknown_IsEscPos(string? id)
    {
        // Это и есть то, что делает обновление пустым для уже настроенных касс: у них
        // в settings.json нового ключа нет вовсе, и они обязаны продолжить работать
        // ровно так же, как работали.
        Assert.Same(DisplayProtocols.EscPos, DisplayProtocols.Resolve(id));
    }

    [Fact]
    public void Resolve_KnownId_IsCaseInsensitive()
    {
        Assert.Same(DisplayProtocols.Cd5220, DisplayProtocols.Resolve("cd5220"));
    }

    [Fact]
    public void Catalog_HoldsFourProtocolsWithDistinctIds()
    {
        Assert.Equal(4, DisplayProtocols.All.Count);
        Assert.Equal(4, DisplayProtocols.All.Select(p => p.Id).Distinct().Count());
        Assert.Same(DisplayProtocols.EscPos, DisplayProtocols.Default);
    }
}
