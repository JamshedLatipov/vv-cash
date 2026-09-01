using System.Linq;
using VvCash.Models;
using VvCash.Models.Receipt;
using VvCash.Services.Rendering;
using Xunit;

namespace VvCash.Tests;

public class EscPosEmitterTest
{
    private static readonly byte[] Init = { 0x1B, 0x40, 0x1C, 0x2E, 0x1B, 0x74, 17 };

    private static byte[] Emit(params ReceiptOp[] ops) =>
        EscPosEmitter.Emit(ops, EscPosCodePages.Cp866);

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
    public void Emit_TurnsBoldOffOnlyWhenSomethingNonBoldFollows()
    {
        var bytes = Emit(
            new BoldOp(true), new TextOp("A"),
            new BoldOp(false), new TextOp("B"));

        Assert.Single(FindAll(bytes, new byte[] { 0x1B, 0x45, 0x01 }));
        Assert.Single(FindAll(bytes, new byte[] { 0x1B, 0x45, 0x00 }));
    }

    [Fact]
    public void Emit_WritesOneLineFeedPerFeedLine()
    {
        var bytes = Emit(new FeedOp(2));

        Assert.Equal(new byte[] { 0x0A, 0x0A }, bytes.Skip(Init.Length).ToArray());
    }

    [Fact]
    public void Emit_WritesTheCutCommand()
    {
        var bytes = Emit(new CutOp());

        Assert.Equal(new byte[] { 0x1D, 0x56, 0x42, 0x00 }, bytes.Skip(Init.Length).ToArray());
    }

    private static int[] FindAll(byte[] haystack, byte[] needle)
    {
        var hits = new System.Collections.Generic.List<int>();
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
