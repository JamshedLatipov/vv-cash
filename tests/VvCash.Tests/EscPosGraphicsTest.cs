using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VvCash.Models;
using VvCash.Models.Receipt;
using VvCash.Services.Rendering;
using Xunit;

namespace VvCash.Tests;

public class EscPosGraphicsTest
{
    private static byte[] Emit(params ReceiptOp[] ops) =>
        EscPosEmitter.Emit(ops, EscPosCodePages.Cp866);

    [Fact]
    public void Qr_EmitsTheFullCommandSequence_InOrder_WithUtf8DataAndErrorCorrectionM()
    {
        // Один Contains на всю последовательность целиком — а не отдельные
        // фрагменты — единственный способ поймать перестановку функций или
        // ошибку в арифметике длины: фрагментные проверки этого не замечают,
        // потому что не требуют, чтобы данные шли СРАЗУ ЗА заголовком.
        //
        // Данные — кириллица: это заодно доказывает, что QR кодируется в
        // UTF-8, а не в кодовую страницу принтера (CP866 дала бы совсем
        // другие байты для "Чек").
        var bytes = Emit(new QrOp("Чек", ModuleSize: 6));

        var data = Encoding.UTF8.GetBytes("Чек");
        var len = data.Length + 3;
        var expected = new List<byte>
        {
            0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00,             // 165: модель 2
            0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, 6,                      // 167: размер модуля
            0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31,                   // 169: уровень коррекции M
            0x1D, 0x28, 0x6B, (byte)(len & 0xFF), (byte)(len >> 8), 0x31, 0x50, 0x30, // 180: данные
        };
        expected.AddRange(data);
        expected.AddRange(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30 }); // 181: печать

        Assert.True(Contains(bytes, expected.ToArray()));
    }

    [Fact]
    public void Barcode_EmitsHeightHriAndTheFullCode128Payload_WithSetBSelectorAndEscaping()
    {
        // "A{B" содержит литеральную "{" — она обязана удвоиться, а перед
        // всем данные обязан идти селектор набора "{B". Без селектора GS k
        // читает данные как обычный текст (см. ревью); без удвоения "{"
        // читалась бы как начало нового селектора.
        var bytes = Emit(new BarcodeOp("A{B", BarcodeSymbology.Code128, Height: 64, PrintHri: true));

        var payload = Encoding.ASCII.GetBytes("{B" + "A{B".Replace("{", "{{"));
        var expected = new List<byte>
        {
            0x1D, 0x68, 64,                        // GS h — высота
            0x1D, 0x48, 0x02,                       // GS H — подпись снизу
            0x1D, 0x6B, 73, (byte)payload.Length,   // GS k, m=73 (CODE128), n=длина payload
        };
        expected.AddRange(payload);

        Assert.True(Contains(bytes, expected.ToArray()));
    }

    [Fact]
    public void Barcode_PrintsEan13Digits_WithoutASetSelector()
    {
        // EAN-13 не пользуется механизмом {A/{B/{C} — это особенность только
        // CODE128. Данные идут как есть, ровно 13 цифр. "4006381333931" —
        // валидная контрольная цифра (проверено вручную по стандартной
        // формуле EAN-13): TryEncodeBarcode теперь её тоже проверяет, и
        // случайные 13 цифр вроде "4600000000000" (контрольная не сходится)
        // отбрасывались бы.
        var bytes = Emit(new BarcodeOp("4006381333931", BarcodeSymbology.Ean13, Height: 50, PrintHri: false));

        var payload = Encoding.ASCII.GetBytes("4006381333931");
        var expected = new List<byte> { 0x1D, 0x6B, 67, (byte)payload.Length };
        expected.AddRange(payload);

        Assert.True(Contains(bytes, expected.ToArray()));
    }

    [Fact]
    public void Barcode_EmitsNoGsK_WhenDataIsNotAscii()
    {
        // Эмиттер — последняя линия обороны на случай, если BarcodeOp собрали
        // в обход рендерера (который такой блок в норме отбрасывает сам, см.
        // ReceiptRendererTest). GS h/GS H всё равно печатаются — высота и HRI
        // не зависят от данных, — но GS k не должен появиться вовсе.
        var bytes = Emit(new BarcodeOp("Чек42", BarcodeSymbology.Code128, Height: 64, PrintHri: true));

        Assert.True(Contains(bytes, new byte[] { 0x1D, 0x68, 64 }));
        Assert.False(Contains(bytes, new byte[] { 0x1D, 0x6B }));
    }

    [Fact]
    public void NvLogo_PrintsTheSlot()
    {
        var bytes = Emit(new NvLogoOp(Slot: 2));

        Assert.True(Contains(bytes, new byte[] { 0x1C, 0x70, 2, 0 }));      // FS p n m
    }

    [Fact]
    public void Bitmap_WritesTheRasterHeaderWithWidthInBytes()
    {
        // GS v 0: ширина задаётся в БАЙТАХ, высота — в точках. Перепутать их
        // местами — получить на бумаге кашу вместо логотипа.
        var raster = new byte[6];                                            // 48 точек × 1 строка
        var bytes = Emit(new BitmapOp(raster, WidthBytes: 6, Height: 1));

        Assert.True(Contains(bytes, new byte[] { 0x1D, 0x76, 0x30, 0x00, 6, 0, 1, 0 }));
    }

    [Fact]
    public void BitmapOp_ThrowsWhenTheRasterDoesNotMatchTheDeclaredSize()
    {
        // Замок против регрессии: инвариант проверялся через `>=`, пропуская
        // растр ДЛИННЕЕ объявленного размера — эмиттер пишет его ВЕСЬ, и
        // лишние байты вместе со следующей командой (например, обрезкой)
        // уезжают в поток как ещё пиксели картинки. Обе стороны — короче и
        // длиннее — обязаны падать.
        Assert.Throws<ArgumentException>(() => new BitmapOp(new byte[2], WidthBytes: 6, Height: 4));   // короче
        Assert.Throws<ArgumentException>(() => new BitmapOp(new byte[10], WidthBytes: 1, Height: 1));  // длиннее
    }

    [Fact]
    public void BitmapOp_ThrowsWhenAWithExpressionBreaksTheInvariant()
    {
        // Классическая ловушка записей: проверка только в конструкторе не
        // держится против `with` — он идёт через копирующий конструктор и
        // init-сеттер, а не через конструктор целиком.
        var original = new BitmapOp(new byte[24], WidthBytes: 6, Height: 4);

        Assert.Throws<ArgumentException>(() => original with { Raster = new byte[1] });
        Assert.Throws<ArgumentException>(() => original with { WidthBytes = 9999 });
    }

    [Theory]
    [InlineData(20)]
    [InlineData(300)]
    public void QrBlock_ClampsModuleSizeToTheSpecMaximum(int input)
    {
        // GS ( k, функция 167 принимает размер модуля 1..16 — не 1..255.
        // "moduleSize": 20 (правдоподобная опечатка) уже вне диапазона
        // задолго до приведения к byte, и верхняя граница обязана быть 16,
        // а не 255.
        Assert.Equal(16, new QrBlock { ModuleSize = input }.ModuleSize);
    }

    [Fact]
    public void BarcodeBlock_ClampsHeightToAByte()
    {
        // GS h принимает высоту 1..255. Без верхнего потолка здесь 1000
        // приведением к byte стало бы 232 — другая высота, а не отказ.
        Assert.Equal(255, new BarcodeBlock { Height = 1000 }.Height);
    }

    [Fact]
    public void LogoBlock_ClampsNvSlotToAByte()
    {
        // FS p n m принимает слот (n) 1..255. Без верхнего потолка здесь
        // 1000 приведением к byte стало бы 232 — чужой слот, а не отказ.
        Assert.Equal(255, new LogoBlock { NvSlot = 1000 }.NvSlot);
    }

    [Fact]
    public void TryEncodeBarcode_RejectsEmptyData()
    {
        Assert.False(EscPosEmitter.TryEncodeBarcode("", BarcodeSymbology.Code128, out _, out _));
    }

    [Theory]
    [InlineData("46000000000")]     // 11 цифр — на одну короче нормы
    [InlineData("46000000000000")]  // 14 цифр — на одну длиннее нормы
    public void TryEncodeBarcode_RejectsEan13WithTheWrongDigitCount(string data)
    {
        Assert.False(EscPosEmitter.TryEncodeBarcode(data, BarcodeSymbology.Ean13, out _, out _));
    }

    [Fact]
    public void TryEncodeBarcode_RejectsEan13WithANonDigitCharacter()
    {
        // Ровно 12 символов (а не 13) — намеренно: при 13 срабатывает ЕЩЁ и
        // проверка контрольной цифры (см. TryEncodeBarcode_RejectsEan13WithAWrongCheckDigit
        // рядом), которая тоже отбросила бы "460000000000A" по своей причине
        // и замаскировала бы дыру именно в проверке алфавита. При 12 цифрах
        // контрольная цифра не проверяется вовсе (её нет), так что здесь
        // остаётся только алфавит.
        Assert.False(EscPosEmitter.TryEncodeBarcode("46000000000A", BarcodeSymbology.Ean13, out _, out _));
    }

    [Fact]
    public void TryEncodeBarcode_RejectsEan13WithAWrongCheckDigit()
    {
        // "4006381333931" — контрольная цифра верна (стандартная формула
        // EAN-13, проверено вручную). Меняем последнюю цифру на заведомо
        // неверную контрольную — 13 цифр, все цифры, длина и алфавит в
        // порядке, но сумма не сходится.
        Assert.False(EscPosEmitter.TryEncodeBarcode("4006381333930", BarcodeSymbology.Ean13, out _, out _));
    }

    [Fact]
    public void TryEncodeBarcode_RejectsDelTheFirstCodeAbovePrintableAscii()
    {
        // 0x7F (DEL) идёт СРАЗУ ЗА верхней границей печатного ASCII (0x7E) —
        // ровно то значение, которое ловит мутацию "<= 0x7E" -> "<= 0x7F".
        var data = "A" + (char)0x7F + "B";

        Assert.False(EscPosEmitter.TryEncodeBarcode(data, BarcodeSymbology.Code128, out _, out _));
    }

    [Fact]
    public void Renderer_EmitsAQrOp_ForAQrBlock_WithSubstitution()
    {
        var t = new ReceiptTemplate
        {
            Blocks = new List<ReceiptBlock> { new QrBlock { Data = "{doc}", ModuleSize = 8 } },
        };
        var sale = new SaleReceiptData(new List<CartItem>(), 0m, 0m, 0m, DocumentNumber: "A-7");

        var qr = ReceiptRenderer.Render(t, sale).OfType<QrOp>().Single();

        Assert.Equal("A-7", qr.Data);
        Assert.Equal(8, qr.ModuleSize);
    }

    [Fact]
    public void Renderer_DropsAQrBlock_WhenItsDataResolvesEmpty()
    {
        // Офлайновая продажа без номера: пустой QR печатать незачем.
        var t = new ReceiptTemplate { Blocks = new List<ReceiptBlock> { new QrBlock { Data = "{doc}" } } };
        var sale = new SaleReceiptData(new List<CartItem>(), 0m, 0m, 0m, DocumentNumber: "");

        Assert.Empty(ReceiptRenderer.Render(t, sale).OfType<QrOp>());
    }

    [Fact]
    public void Renderer_DropsAQrBlock_WhenDataIsLiterallyEmpty_NotJustAnEmptySubstitution()
    {
        // QrBlock.Data по умолчанию "" — блок, добавленный в конструкторе и
        // не заполненный, не должен доехать до принтера. TrySubstitute тут
        // ничего "пустого" не подставляла — подставлять было нечего, строка
        // без "{...}" проходит её как есть, и раньше это было дырой.
        var t = new ReceiptTemplate { Blocks = new List<ReceiptBlock> { new QrBlock() } };

        Assert.Empty(ReceiptRenderer.Render(t, new SaleReceiptData(new List<CartItem>(), 0m, 0m, 0m)).OfType<QrOp>());
    }

    [Fact]
    public void Renderer_EmitsABarcodeOp_ForABarcodeBlock_WithSubstitution()
    {
        var t = new ReceiptTemplate
        {
            Blocks = new List<ReceiptBlock> { new BarcodeBlock { Data = "{doc}", Height = 50, PrintHri = false } },
        };
        var sale = new SaleReceiptData(new List<CartItem>(), 0m, 0m, 0m, DocumentNumber: "4600000000000");

        var bc = ReceiptRenderer.Render(t, sale).OfType<BarcodeOp>().Single();

        Assert.Equal("4600000000000", bc.Data);
        Assert.Equal(50, bc.Height);
        Assert.False(bc.PrintHri);
    }

    [Fact]
    public void Renderer_DropsABarcodeBlock_WhenItsDataResolvesEmpty()
    {
        var t = new ReceiptTemplate { Blocks = new List<ReceiptBlock> { new BarcodeBlock { Data = "{doc}" } } };
        var sale = new SaleReceiptData(new List<CartItem>(), 0m, 0m, 0m, DocumentNumber: "");

        Assert.Empty(ReceiptRenderer.Render(t, sale).OfType<BarcodeOp>());
    }

    [Fact]
    public void Renderer_DropsABarcodeBlock_WhenDataIsLiterallyEmpty()
    {
        var t = new ReceiptTemplate { Blocks = new List<ReceiptBlock> { new BarcodeBlock() } };

        Assert.Empty(ReceiptRenderer.Render(t, new SaleReceiptData(new List<CartItem>(), 0m, 0m, 0m)).OfType<BarcodeOp>());
    }

    [Fact]
    public void Renderer_DropsABarcodeBlock_WhenDataIsNotAscii()
    {
        // CODE128 физически не кодирует кириллицу — печатать "???42" вместо
        // штрихкода хуже, чем не напечатать ничего.
        var t = new ReceiptTemplate { Blocks = new List<ReceiptBlock> { new BarcodeBlock { Data = "Чек42" } } };

        Assert.Empty(ReceiptRenderer.Render(t, new SaleReceiptData(new List<CartItem>(), 0m, 0m, 0m)).OfType<BarcodeOp>());
    }

    [Fact]
    public void Renderer_DropsABarcodeBlock_WhenTheEncodedPayloadWouldExceed255Bytes()
    {
        // 255 девяток без единой "{" — уже 257 байт после служебного
        // префикса "{B". BarcodeBlock.Data не клампится по длине (в отличие
        // от Height/ModuleSize/NvSlot), поэтому это отбрасывает рендерер, а
        // не сеттер модели.
        var longData = new string('9', 255);
        var t = new ReceiptTemplate { Blocks = new List<ReceiptBlock> { new BarcodeBlock { Data = longData } } };

        Assert.Empty(ReceiptRenderer.Render(t, new SaleReceiptData(new List<CartItem>(), 0m, 0m, 0m)).OfType<BarcodeOp>());
    }

    [Fact]
    public void Renderer_DropsABarcodeBlock_WhenEan13DataIsNotTwelveOrThirteenDigits()
    {
        var t = new ReceiptTemplate
        {
            Blocks = new List<ReceiptBlock> { new BarcodeBlock { Data = "A-42", Symbology = BarcodeSymbology.Ean13 } },
        };

        Assert.Empty(ReceiptRenderer.Render(t, new SaleReceiptData(new List<CartItem>(), 0m, 0m, 0m)).OfType<BarcodeOp>());
    }

    [Fact]
    public void Renderer_EmitsAnNvLogoOp_ForAnNvLogoBlock()
    {
        var t = new ReceiptTemplate
        {
            Blocks = new List<ReceiptBlock> { new LogoBlock { Source = LogoSource.Nv, NvSlot = 3 } },
        };

        var logo = ReceiptRenderer.Render(t, new SaleReceiptData(new List<CartItem>(), 0m, 0m, 0m))
            .OfType<NvLogoOp>().Single();

        Assert.Equal(3, logo.Slot);
    }

    [Fact]
    public void Renderer_DropsALogoBlock_WhenSourceIsBitmap()
    {
        // Растровый логотип подключается в Task 9 вместе с опцией
        // receipt_logo: до тех пор блок с этим источником не печатает ничего
        // вовсе — ни NvLogoOp, ни BitmapOp, ни повисшего AlignOp.
        var t = new ReceiptTemplate
        {
            Blocks = new List<ReceiptBlock> { new LogoBlock { Source = LogoSource.Bitmap } },
        };

        var ops = ReceiptRenderer.Render(t, new SaleReceiptData(new List<CartItem>(), 0m, 0m, 0m));

        Assert.Empty(ops);
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length && match; j++)
                match = haystack[i + j] == needle[j];
            if (match) return true;
        }
        return false;
    }
}
