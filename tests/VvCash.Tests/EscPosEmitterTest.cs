using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VvCash.Models;
using VvCash.Models.Receipt;
using VvCash.Services.Rendering;
using Xunit;

namespace VvCash.Tests;

public class EscPosEmitterTest
{
    // Значение селектора берётся из каталога, а не зашито числом: здесь важна
    // только длина префикса (см. Init.Length ниже), а не то, что у CP866
    // сегодня селектор именно 17 — иначе тест вводит в заблуждение о том, что
    // именно он проверяет.
    private static readonly byte[] Init =
        { 0x1B, 0x40, 0x1C, 0x2E, 0x1B, 0x74, EscPosCodePages.Cp866.EscTSelector };

    private static byte[] Emit(params ReceiptOp[] ops) =>
        EscPosEmitter.Emit(ops, EscPosCodePages.Cp866);

    /// <summary>Тип операции, которого эмиттер не знает. ReceiptOp — abstract
    /// record без явного конструктора, и компилятор генерирует для него
    /// protected (не private) конструктор без параметров, так что унаследоваться
    /// можно и из другой сборки — этот тип объявлен здесь ровно для того, чтобы
    /// это продемонстрировать.</summary>
    private sealed record UnknownOp : ReceiptOp;

    [Fact]
    public void Emit_OpensWithInit_CancelKanji_AndCodeTable()
    {
        // Порядок здесь не вкусовщина: в китайском режиме ESC t принтером
        // игнорируется, поэтому FS . обязан идти до него.
        var bytes = Emit(new TextOp("A"));

        Assert.Equal(EscPosCodePages.Cp866.EscTSelector, bytes[6]);
        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1C, 0x2E, 0x1B, 0x74 }, bytes.Take(6).ToArray());
    }

    [Fact]
    public void Emit_WritesTextInTheCodePage_WithATrailingNewline()
    {
        var bytes = Emit(new TextOp("Ok"));

        Assert.Equal(new byte[] { (byte)'O', (byte)'k', 0x0A }, bytes.Skip(Init.Length).ToArray());
    }

    [Fact]
    public void Emit_EncodesTextInTheChosenCodePage_NotUtf8()
    {
        // Кодовая страница — единственная причина, по которой этот класс вообще
        // существует. Тест на одних латинских байтах её бы не заметил: "A", "Ok"
        // и латиница выше кодируются одинаково что в CP866, что в UTF-8.
        var bytes = Emit(new TextOp("Товар"));

        Assert.NotEmpty(FindAll(bytes, EscPosCodePages.Cp866.Encoding.GetBytes("Товар")));
        Assert.Empty(FindAll(bytes, Encoding.UTF8.GetBytes("Товар")));
    }

    [Fact]
    public void Emit_SkipsAlignLeft_BecauseTheInitialStateIsAlreadyLeft()
    {
        // Допущение, на которое опирается вся модель состояния: ESC @
        // сбрасывает выравнивание/emphasized/режим печати к исходному —
        // поэтому эмиттер стартует со счётчиков Left/false/false и не обязан
        // заново объявлять их первой же командой. Допущение не универсально:
        // CmdCancelKanji рядом (и её двойник в EscPosPrinterService.cs)
        // существует именно потому, что ESC @ на XP-80 НЕ сбрасывает китайский
        // режим. Здесь же фиксируем тестом, что для align/bold/doubleSize
        // допущение выполняется.
        var bytes = Emit(new AlignOp(ReceiptAlign.Left), new TextOp("A"));

        Assert.Equal(new byte[] { (byte)'A', 0x0A }, bytes.Skip(Init.Length).ToArray());
    }

    [Fact]
    public void Emit_SkipsAnAlignCommand_WhenTheAlignmentIsAlreadyInEffect()
    {
        // Ради этого свойства эмиттер и ведёт состояние: без него блочная
        // раскладка выдала бы лишнюю ESC a на каждый блок, и байт-в-байт
        // совпадения с нынешним чеком не вышло бы никогда.
        var bytes = Emit(
            new AlignOp(ReceiptAlign.Center), new TextOp("A"),
            new AlignOp(ReceiptAlign.Center), new TextOp("B"));

        Assert.Single(FindAll(bytes, new byte[] { 0x1B, 0x61, 0x01 }));
    }

    [Fact]
    public void Emit_EmitsAlignLeft_WhenTheAlignmentActuallyChanges()
    {
        var bytes = Emit(
            new AlignOp(ReceiptAlign.Center), new TextOp("A"),
            new AlignOp(ReceiptAlign.Left), new TextOp("B"));

        Assert.Single(FindAll(bytes, new byte[] { 0x1B, 0x61, 0x00 }));
    }

    [Fact]
    public void Emit_EmitsAlignRight_WhenTheAlignmentChangesToRight()
    {
        // Right не задет ни одним из предыдущих тестов — ветка была мертва.
        var bytes = Emit(new AlignOp(ReceiptAlign.Right), new TextOp("A"));

        Assert.Single(FindAll(bytes, new byte[] { 0x1B, 0x61, 0x02 }));
    }

    [Fact]
    public void Emit_EmitsAlignCenterTwice_WhenTheAlignmentReturnsToItAfterLeft()
    {
        // Подавление обязано смотреть только на непосредственно предыдущее
        // состояние, а не помнить, что Center уже встречался где-то раньше —
        // иначе Center -> Left -> Center дал бы одну команду вместо двух.
        var bytes = Emit(
            new AlignOp(ReceiptAlign.Center), new TextOp("A"),
            new AlignOp(ReceiptAlign.Left), new TextOp("B"),
            new AlignOp(ReceiptAlign.Center), new TextOp("C"));

        Assert.Equal(2, FindAll(bytes, new byte[] { 0x1B, 0x61, 0x01 }).Length);
    }

    [Fact]
    public void Emit_EmitsBoldOnThenBoldOff_WhenBoldTogglesOnAndOff()
    {
        var bytes = Emit(
            new BoldOp(true), new TextOp("A"),
            new BoldOp(false), new TextOp("B"));

        Assert.Single(FindAll(bytes, new byte[] { 0x1B, 0x45, 0x01 }));
        Assert.Single(FindAll(bytes, new byte[] { 0x1B, 0x45, 0x00 }));
    }

    [Fact]
    public void Emit_SkipsABoldCommand_WhenBoldIsAlreadyInEffect()
    {
        // Тот же принцип подавления, что и у AlignOp, но у BoldOp он не был
        // проверен отдельно.
        var bytes = Emit(
            new BoldOp(true), new TextOp("A"),
            new BoldOp(true), new TextOp("B"));

        Assert.Single(FindAll(bytes, new byte[] { 0x1B, 0x45, 0x01 }));
    }

    [Fact]
    public void Emit_WritesDoubleSizeOnAndOff()
    {
        // DoubleSizeOp не был задет ни одним тестом — опечатка в одной из
        // констант ESC ! прошла бы мимо всего набора.
        var bytes = Emit(
            new DoubleSizeOp(true), new TextOp("A"),
            new DoubleSizeOp(false), new TextOp("B"));

        Assert.Single(FindAll(bytes, new byte[] { 0x1B, 0x21, 0x30 }));
        Assert.Single(FindAll(bytes, new byte[] { 0x1B, 0x21, 0x00 }));
    }

    [Fact]
    public void Emit_SkipsADoubleSizeCommand_WhenTheSizeIsAlreadyInEffect()
    {
        var bytes = Emit(
            new DoubleSizeOp(true), new TextOp("A"),
            new DoubleSizeOp(true), new TextOp("B"));

        Assert.Single(FindAll(bytes, new byte[] { 0x1B, 0x21, 0x30 }));
    }

    [Fact]
    public void Emit_ReassertsBoldOnlyOnce_WhenDoubleSizeRepeatsWithoutChanging()
    {
        // Зеркало Emit_SkipsADoubleSizeCommand_WhenTheSizeIsAlreadyInEffect,
        // но с включённым жирным. Обе ветки DoubleSizeOp похожи почти
        // дословно и напрашиваются на слияние в одну — но переиздание ESC E
        // обязано жить только в ветке РЕАЛЬНОЙ смены состояния (Task 2,
        // фикс "re-assert bold after ESC !"). Уедь оно в подавленную ветку —
        // блочный рендерер, объявляющий размер на каждом блоке, получит
        // лишнюю ESC E 1 на каждый повторный DoubleSizeOp, а повторных у
        // него будет большинство.
        var bytes = Emit(
            new BoldOp(true),
            new DoubleSizeOp(true), new TextOp("A"),
            new DoubleSizeOp(true), new TextOp("B"),
            new DoubleSizeOp(true), new TextOp("C"));

        // Исходное включение плюс одно переиздание — для единственной
        // настоящей смены состояния (false -> true). Оба повтора
        // DoubleSizeOp(true) не пишут ни ESC !, ни ESC E.
        Assert.Equal(2, FindAll(bytes, new byte[] { 0x1B, 0x45, 0x01 }).Length);
        Assert.Single(FindAll(bytes, new byte[] { 0x1B, 0x21, 0x30 }));
    }

    [Fact]
    public void Emit_ReassertsBold_AfterDoubleSizeResetsTheWholePrintMode()
    {
        // ESC ! адресует режим печати ЦЕЛИКОМ одной битовой маской (шрифт,
        // emphasized, double-height, double-width, underline), а не только
        // размер. И CmdDoubleSizeOn (0x30), и CmdDoubleSizeOff (0x00) несут
        // нулевой бит emphasized, поэтому DoubleSizeOp гасит жирный на бумаге
        // независимо от того, что просил BoldOp. Блочная раскладка с сервера
        // порядок блоков не гарантирует — Bold и DoubleSize вполне могут
        // прийти вперемешку в любом порядке, так это и обнаружилось: "still
        // bold?" в этой самой последовательности выходило обычным шрифтом до
        // исправления.
        var bytes = Emit(
            new BoldOp(true), new TextOp("bold"),
            new DoubleSizeOp(true), new TextOp("BIG"),
            new DoubleSizeOp(false), new TextOp("still bold?"));

        // Три раза: изначальное включение плюс переиздание после каждого из
        // двух ESC ! (DoubleSizeOp(true) и DoubleSizeOp(false)).
        Assert.Equal(3, FindAll(bytes, new byte[] { 0x1B, 0x45, 0x01 }).Length);
        // BoldOp(false) в сценарии не встречается — выключать жирный не просили.
        Assert.Empty(FindAll(bytes, new byte[] { 0x1B, 0x45, 0x00 }));
    }

    [Fact]
    public void Emit_WritesOneLineFeedPerFeedLine()
    {
        var bytes = Emit(new FeedOp(2));

        Assert.Equal(new byte[] { 0x0A, 0x0A }, bytes.Skip(Init.Length).ToArray());
    }

    [Fact]
    public void Emit_TreatsANegativeFeedCountAsZero()
    {
        // Lines приезжает из JSON шаблона с сервера, то есть это внешний ввод.
        // Кривое отрицательное число в косметическом поле не должно ронять
        // чек — фиксируем тестом, что это осознанное поведение, а не
        // недосмотр, который однажды исправят панической проверкой.
        var bytes = Emit(new FeedOp(-3));

        Assert.Equal(Init, bytes);
    }

    [Fact]
    public void Emit_WritesTheCutCommand()
    {
        var bytes = Emit(new CutOp());

        Assert.Equal(new byte[] { 0x1D, 0x56, 0x42, 0x00 }, bytes.Skip(Init.Length).ToArray());
    }

    [Fact]
    public void Emit_ThrowsNotSupported_ForAnUnhandledOpType()
    {
        // switch-оператор в C# не проверяется компилятором на полноту: новый
        // тип операции, забытый в switch эмиттера, скомпилируется молча и
        // упадёт только на кассе при попытке напечатать чек. default обязан
        // быть живой веткой, закреплённой тестом, а не мёртвым кодом, который
        // однажды снесут как недостижимый.
        var ex = Assert.Throws<NotSupportedException>(() => Emit(new UnknownOp()));
        Assert.Contains(nameof(UnknownOp), ex.Message);
    }

    private static int[] FindAll(byte[] haystack, byte[] needle)
    {
        var hits = new List<int>();
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length && match; j++)
                match = haystack[i + j] == needle[j];
            if (match) hits.Add(i);
        }
        return hits.ToArray();
    }
}
