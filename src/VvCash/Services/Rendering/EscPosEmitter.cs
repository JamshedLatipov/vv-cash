using System;
using System.Collections.Generic;
using System.IO;
using VvCash.Models;
using VvCash.Models.Receipt;

namespace VvCash.Services.Rendering;

/// <summary>Единственное место, знающее про ESC/POS. Всё, что выше, оперирует
/// операциями и ничего не знает ни про байты, ни про кодовую страницу.
///
/// Эмиттер ведёт состояние принтера и не повторяет команду, которая уже в силе.
/// Это не микрооптимизация: блочная раскладка объявляет выравнивание на каждом
/// блоке, и без слежения чек получал бы ESC a перед каждой строкой — то есть
/// отличался бы от нынешнего байтами, и замок совместимости из Task 1 сойтись
/// не мог бы в принципе.</summary>
public static class EscPosEmitter
{
    private static readonly byte[] CmdInit = { 0x1B, 0x40 };
    private static readonly byte[] CmdCancelKanji = { 0x1C, 0x2E };
    private static readonly byte[] CmdSelectCodeTable = { 0x1B, 0x74 };
    private static readonly byte[] CmdAlignLeft = { 0x1B, 0x61, 0x00 };
    private static readonly byte[] CmdAlignCenter = { 0x1B, 0x61, 0x01 };
    private static readonly byte[] CmdAlignRight = { 0x1B, 0x61, 0x02 };
    private static readonly byte[] CmdBoldOn = { 0x1B, 0x45, 0x01 };
    private static readonly byte[] CmdBoldOff = { 0x1B, 0x45, 0x00 };
    private static readonly byte[] CmdDoubleSizeOn = { 0x1B, 0x21, 0x30 };
    private static readonly byte[] CmdDoubleSizeOff = { 0x1B, 0x21, 0x00 };
    private static readonly byte[] CmdCut = { 0x1D, 0x56, 0x42, 0x00 };

    public static byte[] Emit(IEnumerable<ReceiptOp> ops, EscPosCodePage codePage)
    {
        using var ms = new MemoryStream();

        ms.Write(CmdInit, 0, CmdInit.Length);
        // Строго до ESC t: в китайском режиме выбор таблицы принтером не
        // рассматривается, и порядок здесь — не вкусовщина.
        ms.Write(CmdCancelKanji, 0, CmdCancelKanji.Length);
        ms.Write(CmdSelectCodeTable, 0, CmdSelectCodeTable.Length);
        ms.WriteByte(codePage.EscTSelector);

        var align = ReceiptAlign.Left;
        var bold = false;
        var doubleSize = false;

        foreach (var op in ops)
        {
            switch (op)
            {
                case AlignOp a when a.Align != align:
                    align = a.Align;
                    var cmd = align switch
                    {
                        ReceiptAlign.Center => CmdAlignCenter,
                        ReceiptAlign.Right => CmdAlignRight,
                        _ => CmdAlignLeft,
                    };
                    ms.Write(cmd, 0, cmd.Length);
                    break;

                case AlignOp:
                    break;

                case BoldOp b when b.On != bold:
                    bold = b.On;
                    var boldCmd = bold ? CmdBoldOn : CmdBoldOff;
                    ms.Write(boldCmd, 0, boldCmd.Length);
                    break;

                case BoldOp:
                    break;

                case DoubleSizeOp d when d.On != doubleSize:
                    doubleSize = d.On;
                    var sizeCmd = doubleSize ? CmdDoubleSizeOn : CmdDoubleSizeOff;
                    ms.Write(sizeCmd, 0, sizeCmd.Length);
                    break;

                case DoubleSizeOp:
                    break;

                case TextOp t:
                    var bytes = codePage.Encoding.GetBytes(t.Line + "\n");
                    ms.Write(bytes, 0, bytes.Length);
                    break;

                case FeedOp f:
                    for (var i = 0; i < f.Lines; i++) ms.WriteByte(0x0A);
                    break;

                case CutOp:
                    ms.Write(CmdCut, 0, CmdCut.Length);
                    break;

                default:
                    throw new NotSupportedException($"Неизвестная операция печати: {op.GetType().Name}");
            }
        }

        return ms.ToArray();
    }
}
