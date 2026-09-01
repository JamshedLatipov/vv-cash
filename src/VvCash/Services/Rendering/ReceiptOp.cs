using VvCash.Models.Receipt;

namespace VvCash.Services.Rendering;

/// <summary>Одно действие принтера, ещё не превращённое в байты. Промежуточный
/// слой существует ради двух вещей: раскладка тестируется как текст, а не как
/// байты, и ширина ленты становится параметром одного слоя вместо восьми
/// литералов по коду.</summary>
public abstract record ReceiptOp;

/// <summary>Ровно одна строка на печать; эмиттер сам добавляет завершающий
/// перевод строки. <see cref="Line"/> не должна содержать "\n": такая строка
/// пройдёт эмиттер насквозь и превратится на бумаге в две строки, минуя перенос
/// по ширине ленты и любую логику раскладки, которая эту ширину считает.
/// Разбиение длинных строк — дело слоя раскладки, не этого типа и не
/// эмиттера.</summary>
public sealed record TextOp(string Line) : ReceiptOp;

public sealed record AlignOp(ReceiptAlign Align) : ReceiptOp;

public sealed record BoldOp(bool On) : ReceiptOp;

public sealed record DoubleSizeOp(bool On) : ReceiptOp;

public sealed record FeedOp(int Lines) : ReceiptOp;

public sealed record CutOp : ReceiptOp;

public sealed record QrOp(string Data, int ModuleSize) : ReceiptOp;

public sealed record BarcodeOp(string Data, BarcodeSymbology Symbology, int Height, bool PrintHri) : ReceiptOp;

/// <summary>Логотип, уже прошитый в память принтера. Байтов на ленте — четыре.</summary>
public sealed record NvLogoOp(int Slot) : ReceiptOp;

/// <summary>Растр, приехавший с сервера уже сведённым в один бит. Ширина здесь
/// в БАЙТАХ, высота в точках — так требует GS v 0, и путать их нельзя.</summary>
public sealed record BitmapOp(byte[] Raster, int WidthBytes, int Height) : ReceiptOp;
