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
