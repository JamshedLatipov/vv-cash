using System.Collections.Generic;
using System.Linq;
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
    public void Qr_SelectsModel2_SetsTheModuleSize_StoresTheData_ThenPrints()
    {
        var bytes = Emit(new QrOp("A-42", ModuleSize: 6));

        // GS ( k, функция 165 — модель; 167 — размер модуля; 180 — печать.
        Assert.True(Contains(bytes, new byte[] { 0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00 }));
        Assert.True(Contains(bytes, new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, 6 }));
        Assert.True(Contains(bytes, new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30 }));
        Assert.True(Contains(bytes, System.Text.Encoding.ASCII.GetBytes("A-42")));
    }

    [Fact]
    public void Barcode_SetsHeightAndHri_ThenPrintsCode128()
    {
        var bytes = Emit(new BarcodeOp("12345678", BarcodeSymbology.Code128, Height: 64, PrintHri: true));

        Assert.True(Contains(bytes, new byte[] { 0x1D, 0x68, 64 }));       // GS h — высота
        Assert.True(Contains(bytes, new byte[] { 0x1D, 0x48, 0x02 }));      // GS H — подпись снизу
        Assert.True(Contains(bytes, new byte[] { 0x1D, 0x6B, 73 }));        // GS k m=73 — Code128
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
