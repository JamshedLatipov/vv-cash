using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // GS ( k, функция 165: модель 2 (0x32) — единственная, которую понимают
    // все ходовые аппараты. Хвостовой 0x00 — это n самой функции 165, к
    // модели отношения не имеет.
    private static readonly byte[] QrSelectModel2 = { 0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00 };
    // Функция 167 без последнего байта: он один меняется от QrOp к QrOp
    // (размер модуля), остальные шесть — нет.
    private static readonly byte[] QrModuleSizePrefix = { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43 };
    // Функция 169: уровень коррекции ошибок. n=0x31 (49) — уровень M (15%), а
    // не дефолтный после ESC @ уровень L (7%): чек живёт в кармане, мнётся и
    // выцветает, и 7% избыточности для него мало.
    private static readonly byte[] QrErrorCorrectionLevelM = { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31 };
    // Функция 181: напечатать то, что уже сложено в буфер символа функцией 180.
    private static readonly byte[] QrPrint = { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30 };

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
        // -1 и null — недостижимые значения (Height клампится к [1, 255] в
        // BarcodeBlock, а у PrintHri третьего состояния нет), поэтому первый
        // же штрихкод в чеке гарантированно переиздаёт GS h и GS H. В отличие
        // от align/bold/doubleSize чуть выше, для которых ESC @ документирует
        // сброс в конкретное состояние (Left/false/false), для высоты
        // штрихкода и режима HRI такого гарантированного стартового
        // состояния принтера нет — подавлять первую команду не на чем.
        var barcodeHeight = -1;
        bool? barcodePrintHri = null;

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

                case QrOp qr:
                    WriteQr(ms, qr);
                    break;

                case BarcodeOp bc:
                    if (bc.Height != barcodeHeight)
                    {
                        barcodeHeight = bc.Height;
                        ms.Write(new byte[] { 0x1D, 0x68, (byte)barcodeHeight }, 0, 3);   // GS h — высота
                    }
                    if (bc.PrintHri != barcodePrintHri)
                    {
                        barcodePrintHri = bc.PrintHri;
                        // GS H — подпись снизу
                        ms.Write(new byte[] { 0x1D, 0x48, bc.PrintHri ? (byte)0x02 : (byte)0x00 }, 0, 3);
                    }
                    WriteBarcode(ms, bc);
                    break;

                case NvLogoOp nv:
                    ms.Write(new byte[] { 0x1C, 0x70, (byte)nv.Slot, 0 }, 0, 4);
                    break;

                case BitmapOp bmp:
                    ms.Write(new byte[]
                    {
                        0x1D, 0x76, 0x30, 0x00,
                        (byte)(bmp.WidthBytes & 0xFF), (byte)(bmp.WidthBytes >> 8),
                        (byte)(bmp.Height & 0xFF), (byte)(bmp.Height >> 8),
                    }, 0, 8);
                    ms.Write(bmp.Raster, 0, bmp.Raster.Length);
                    break;

                default:
                    throw new NotSupportedException($"Неизвестная операция печати: {op.GetType().Name}");
            }
        }

        return ms.ToArray();
    }

    /// <summary>GS ( k четырьмя вызовами: выбрать модель, задать размер
    /// модуля, задать уровень коррекции ошибок, сложить данные в буфер
    /// символа, напечатать. Порядок обязателен — печать берёт то, что лежит
    /// в буфере на её момент.</summary>
    private static void WriteQr(MemoryStream ms, QrOp qr)
    {
        ms.Write(QrSelectModel2, 0, QrSelectModel2.Length);

        ms.Write(QrModuleSizePrefix, 0, QrModuleSizePrefix.Length);
        ms.WriteByte((byte)qr.ModuleSize);

        ms.Write(QrErrorCorrectionLevelM, 0, QrErrorCorrectionLevelM.Length);

        // QR читает чужой сканер, а не наш принтер — кодовая страница
        // принтера (CP866/CP1251) для сканера чужой алфавит: "Чек" в CP866 —
        // это байты 97 A5 AA, и ни один сканер так не прочитает. UTF-8 — то,
        // что ждёт любой сканер по умолчанию. Без указателя ECI (Extended
        // Channel Interpretation) в самих данных символа — это отраслевая
        // норма для не-ASCII QR, а не недосмотр: массовые сканеры (в том
        // числе телефонные камеры) при отсутствии ECI сами пробуют UTF-8 как
        // наиболее вероятную кодировку.
        var data = System.Text.Encoding.UTF8.GetBytes(qr.Data);
        var len = data.Length + 3;
        // Функция 180: сложить данные. pL/pH считают ТРИ служебных байта следом,
        // а не только полезную нагрузку — отсюда +3.
        ms.Write(new byte[] { 0x1D, 0x28, 0x6B, (byte)(len & 0xFF), (byte)(len >> 8), 0x31, 0x50, 0x30 }, 0, 8);
        ms.Write(data, 0, data.Length);

        ms.Write(QrPrint, 0, QrPrint.Length);
    }

    private static void WriteBarcode(MemoryStream ms, BarcodeOp bc)
    {
        // Рендерер уже вызвал TryEncodeBarcode и отбросил блок целиком при
        // провале, с логом (см. ReceiptRenderer.RenderBlock) — сюда попадают
        // только уже проверенные данные. Повторный провал здесь означает, что
        // BarcodeOp собрали в обход рендерера: тогда не написать GS k вовсе
        // безопаснее, чем написать обрезанный или пустой штрихкод.
        if (!TryEncodeBarcode(bc.Data, bc.Symbology, out var data, out _)) return;

        // m ≥ 65 — форма с длиной вместо NUL-терминатора: она принимает данные с
        // любым байтом внутри, включая ноль, и не зависит от терминатора.
        var m = bc.Symbology == BarcodeSymbology.Ean13 ? (byte)67 : (byte)73;
        ms.Write(new byte[] { 0x1D, 0x6B, m, (byte)data.Length }, 0, 4);
        ms.Write(data, 0, data.Length);
    }

    /// <summary>Единственное место, знающее, как GS k кодирует данные для
    /// двух поддерживаемых символик. Рендерер зовёт её, чтобы решить,
    /// печатать ли блок вообще (см. ReceiptRenderer.RenderBlock); эмиттер
    /// зовёт её же перед тем, как писать GS k, чтобы не разойтись с
    /// рендерером в деталях кодирования.
    ///
    /// "ASCII" здесь — печатный ASCII 0x20–0x7E, а не весь диапазон 0–127:
    /// набор B у CODE128 физически несёт только эти байты, управляющих
    /// символов среди них нет — тот же практический запрет, что и на
    /// кириллицу, только про другую часть алфавита.</summary>
    internal static bool TryEncodeBarcode(string data, BarcodeSymbology symbology, out byte[] bytes, out string? reason)
    {
        bytes = Array.Empty<byte>();

        if (string.IsNullOrEmpty(data))
        {
            reason = "данные пусты";
            return false;
        }

        if (!data.All(IsPrintableAscii))
        {
            reason = "данные вне печатного ASCII — ни CODE128, ни EAN-13 такое не кодируют";
            return false;
        }

        if (symbology == BarcodeSymbology.Ean13)
        {
            if (data.Length is not (12 or 13) || !data.All(char.IsAsciiDigit))
            {
                reason = $"EAN-13 требует ровно 12 или 13 цифр, получено \"{data}\"";
                return false;
            }

            // При 13 цифрах последняя — контрольная. Калитка объявлена
            // единственным местом, знающим формат EAN-13 (см. класс выше) —
            // значит контрольную цифру считать ей же, а не понадеяться на
            // принтер: поведение конкретной модели при несходящейся сумме не
            // документировано и не проверено (нет принтера под рукой).
            if (data.Length == 13 && !HasValidEan13CheckDigit(data))
            {
                reason = $"EAN-13 контрольная цифра не сходится: \"{data}\"";
                return false;
            }

            bytes = System.Text.Encoding.ASCII.GetBytes(data);
            reason = null;
            return true;
        }

        // CODE128, набор B: GS k при m=73 требует, чтобы данные начинались с
        // селектора набора ({A/{B/{C) — без него принтер прекращает разбор
        // команды на первом байте и печатает всё как обычный текст.
        // Литеральная "{" внутри данных обязана удваиваться, иначе сама
        // читается как начало нового селектора.
        var payload = "{B" + data.Replace("{", "{{");
        var payloadBytes = System.Text.Encoding.ASCII.GetBytes(payload);
        if (payloadBytes.Length > byte.MaxValue)
        {
            // Предел формата, а не гарантия того, что символ влезет на
            // ленту: GS k хранит длину payload'а в одном байте, и это всё,
            // что здесь проверено. CODE128 набора B кодирует один символ
            // данных в 11 модулей (плюс старт/стоп/checksum) — уже сорок с
            // небольшим символов не умещаются по ширине в стандартную
            // 80-мм печатающую головку при типичной плотности печати.
            // Команда за этим гейтом гарантированно ВАЛИДНА для GS k, но не
            // гарантированно ПОМЕЩАЕТСЯ на бумаге — это тот же класс
            // молчаливого отказа, что и остальные в этом методе, просто
            // сдвинутый на уровень выше (с протокола на физику ленты), и
            // сегодня не проверяется вовсе.
            reason = $"CODE128 после служебного префикса и удвоения '{{' занимает {payloadBytes.Length} байт " +
                     $"— GS k хранит длину в одном байте (максимум {byte.MaxValue})";
            return false;
        }

        bytes = payloadBytes;
        reason = null;
        return true;
    }

    /// <summary>Стандартная контрольная сумма EAN-13: нечётные позиции (0-based)
    /// весом 1, чётные — весом 3, сумма по первым 12 цифрам, дополнение до
    /// ближайшего кратного 10 — и есть 13-я цифра.</summary>
    private static bool HasValidEan13CheckDigit(string digits)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (digits[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10 == digits[12] - '0';
    }

    private static bool IsPrintableAscii(char c) => c is >= (char)0x20 and <= (char)0x7E;
}
