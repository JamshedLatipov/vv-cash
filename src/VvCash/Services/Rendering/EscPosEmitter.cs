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
    // Смысл этой команды — обход того, что ESC @ не выключает китайский
    // режим на XP-80 — подробно задокументирован у одноимённой константы в
    // EscPosPrinterService.cs; здесь не повторяем, а ссылаемся. Два
    // источника правды сохранятся, пока пять старых билдеров печати живут
    // на собственных константах отдельно от этого эмиттера.
    private static readonly byte[] CmdCancelKanji = { 0x1C, 0x2E };
    private static readonly byte[] CmdSelectCodeTable = { 0x1B, 0x74 };
    private static readonly byte[] CmdAlignLeft = { 0x1B, 0x61, 0x00 };
    private static readonly byte[] CmdAlignCenter = { 0x1B, 0x61, 0x01 };
    private static readonly byte[] CmdAlignRight = { 0x1B, 0x61, 0x02 };
    private static readonly byte[] CmdBoldOn = { 0x1B, 0x45, 0x01 };
    private static readonly byte[] CmdBoldOff = { 0x1B, 0x45, 0x00 };
    // ESC ! n — «Select print mode»: выбор режима печати ЦЕЛИКОМ одной
    // битовой маской (шрифт, emphasized, double-height, double-width,
    // underline), а НЕ только размера символа. У обоих значений ниже —
    // 0x30 (double-height + double-width) и 0x00 (все биты сброшены) — бит
    // emphasized (0x08) нулевой. Значит эта команда гасит жирный на
    // принтере независимо от того, что просил BoldOp. Модель состояния
    // эмиттера при этом не путается: переменная bold остаётся тем, что
    // попросили последним, — путается физический принтер. Поэтому switch
    // ниже переиздаёт ESC E следом за каждой из этих двух команд, если
    // жирный должен оставаться в силе.
    private static readonly byte[] CmdDoubleSizeOn = { 0x1B, 0x21, 0x30 };
    private static readonly byte[] CmdDoubleSizeOff = { 0x1B, 0x21, 0x00 };
    private static readonly byte[] CmdCut = { 0x1D, 0x56, 0x42, 0x00 };
    private static readonly byte[] CmdLineFeed = { 0x0A };

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
                    // ESC ! только что сбросил emphasized на принтере (см. док у
                    // CmdDoubleSizeOn/Off выше), независимо от нашей модели bold.
                    // Переиздаём ESC E, если жирный должен оставаться в силе —
                    // иначе текст после DoubleSizeOp молча вышел бы не жирным.
                    //
                    // Эта строка лечит один бит из пяти, которые несёт ESC !
                    // (шрифт, emphasized, double-height, double-width,
                    // underline) — здесь же придётся переиздавать ЛЮБОЙ
                    // будущий режимный атрибут, отображённый в этот же байт.
                    // Появится, скажем, UnderlineOp — без такой же строки для
                    // него здесь вернётся ровно этот баг тем же молчаливым
                    // способом.
                    if (bold)
                        ms.Write(CmdBoldOn, 0, CmdBoldOn.Length);
                    break;

                case DoubleSizeOp:
                    break;

                case TextOp t:
                    var bytes = codePage.Encoding.GetBytes(t.Line + "\n");
                    ms.Write(bytes, 0, bytes.Length);
                    break;

                case FeedOp f:
                    // Lines приходит из JSON шаблона с сервера — внешний ввод.
                    // Отрицательное осознанно трактуем как ноль вместо того,
                    // чтобы бросать: цикл ниже просто не выполнится, а кривое
                    // число в косметическом поле не должно ронять чек.
                    for (var i = 0; i < f.Lines; i++) ms.Write(CmdLineFeed, 0, CmdLineFeed.Length);
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
