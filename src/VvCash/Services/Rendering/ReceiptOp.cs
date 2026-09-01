using VvCash.Models.Receipt;

namespace VvCash.Services.Rendering;

/// <summary>Одно действие принтера, ещё не превращённое в байты. Промежуточный
/// слой существует ради двух вещей: раскладка тестируется как текст, а не как
/// байты, и ширина ленты становится параметром одного слоя вместо восьми
/// литералов по коду.</summary>
public abstract record ReceiptOp;

public sealed record TextOp(string Line) : ReceiptOp;

public sealed record AlignOp(ReceiptAlign Align) : ReceiptOp;

public sealed record BoldOp(bool On) : ReceiptOp;

public sealed record DoubleSizeOp(bool On) : ReceiptOp;

public sealed record FeedOp(int Lines) : ReceiptOp;

public sealed record CutOp : ReceiptOp;
