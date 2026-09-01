using System.Linq;
using VvCash.Models.Receipt;
using Xunit;

namespace VvCash.Tests;

public class ReceiptTemplateTest
{
    [Fact]
    public void Parse_ReadsBlocksByTheirTypeDiscriminator()
    {
        var json = """
        {"version":1,"width":42,"blocks":[
          {"type":"text","content":"Магазин","align":"center","bold":true},
          {"type":"line","char":"=","count":10},
          {"type":"feed","lines":3}
        ]}
        """;

        var t = ReceiptTemplate.Parse(json);

        Assert.Equal(42, t.Width);
        var text = Assert.IsType<TextBlock>(t.Blocks[0]);
        Assert.Equal("Магазин", text.Content);
        Assert.Equal(ReceiptAlign.Center, text.Align);
        Assert.True(text.Bold);
        var line = Assert.IsType<LineBlock>(t.Blocks[1]);
        Assert.Equal("=", line.Char);
        Assert.Equal(10, line.Count);
        Assert.Equal(3, Assert.IsType<FeedBlock>(t.Blocks[2]).Lines);
    }

    [Fact]
    public void Parse_FallsBackToTheDefault_OnBrokenJson()
    {
        // В configs.val у существующих тенантов может лежать что угодно: опция
        // receiptTemplate засеяна в 2019 и шесть лет рендерилась текстовым полем.
        var t = ReceiptTemplate.Parse("не json вовсе");

        Assert.Same(ReceiptTemplate.Default, t);
    }

    [Fact]
    public void Parse_FallsBackToTheDefault_OnEmptyValue()
    {
        Assert.Same(ReceiptTemplate.Default, ReceiptTemplate.Parse(""));
        Assert.Same(ReceiptTemplate.Default, ReceiptTemplate.Parse(null));
    }

    [Fact]
    public void Parse_DropsAnUnknownBlockType_AndKeepsTheRest()
    {
        // Касса терпит блок из более новой админки, чем её собственная сборка.
        // Сервер такой type записать не даст, но обновляются они врозь.
        var json = """
        {"version":1,"width":32,"blocks":[
          {"type":"text","content":"A"},
          {"type":"hologram","spin":"fast"},
          {"type":"text","content":"B"}
        ]}
        """;

        var t = ReceiptTemplate.Parse(json);

        Assert.Equal(2, t.Blocks.Count);
        Assert.Equal(new[] { "A", "B" }, t.Blocks.Cast<TextBlock>().Select(b => b.Content));
    }

    [Fact]
    public void Parse_IgnoresAnUnknownFieldInsideAKnownBlock()
    {
        var t = ReceiptTemplate.Parse("""{"version":1,"blocks":[{"type":"text","content":"A","glitter":true}]}""");

        Assert.Equal("A", Assert.IsType<TextBlock>(t.Blocks[0]).Content);
    }

    [Fact]
    public void Parse_FallsBackToTheDefault_OnAFutureVersion()
    {
        // Несовместимый формат лучше не печатать вовсе, чем печатать наполовину.
        Assert.Same(ReceiptTemplate.Default, ReceiptTemplate.Parse("""{"version":99,"blocks":[]}"""));
    }

    [Fact]
    public void Parse_ClampsANonPositiveWidth_ToTheDefault()
    {
        // Ширина приезжает числом из JSON. Ноль заставил бы Truncate молча съесть
        // название акции, отрицательное — уронить печать целиком.
        Assert.Equal(32, ReceiptTemplate.Parse("""{"version":1,"width":0,"blocks":[]}""").Width);
        Assert.Equal(32, ReceiptTemplate.Parse("""{"version":1,"width":-5,"blocks":[]}""").Width);
    }

    [Fact]
    public void Width_ClampsANonPositiveValue_WhateverTheEntryPoint()
    {
        // Кламп живёт в сеттере именно затем, чтобы объектный инициализатор его не
        // обошёл: разбор JSON — не единственный вход, так пишут и тесты, и код.
        Assert.Equal(32, new ReceiptTemplate { Width = 0 }.Width);
        Assert.Equal(32, new ReceiptTemplate { Width = -5 }.Width);
        Assert.Equal(42, new ReceiptTemplate { Width = 42 }.Width);
    }

    [Fact]
    public void Default_IsThirtyTwoColumnsWide()
    {
        Assert.Equal(32, ReceiptTemplate.Default.Width);
    }

    [Fact]
    public void Default_BlocksAreAllEnabled()
    {
        Assert.All(ReceiptTemplate.Default.Blocks, b => Assert.True(b.Enabled));
    }
}
