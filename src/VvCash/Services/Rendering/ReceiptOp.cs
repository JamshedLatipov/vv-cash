using System;
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
/// в БАЙТАХ, высота в точках — так требует GS v 0, и путать их нельзя.
///
/// Инвариант — раз и навсегда здесь, а не у каждого будущего вызывающего: растр
/// КОРОЧЕ ИЛИ ДЛИННЕЕ WidthBytes×Height (не только короче — эмиттер пишет
/// РОВНО <see cref="Raster"/> целиком, ни байтом больше и не меньше) даёт на
/// бумаге ту же беду, что и перепутанные местами ширина и высота выше: лишние
/// байты растра GS v 0 не отбросит — они и команда следом (например, обрезка)
/// уедут в поток как ещё пиксели картинки. Отсюда `==`, а не `>=`.
///
/// WidthBytes и Height ограничены 0..65535 порознь — это ровно то, что несёт
/// каждое из двухбайтовых полей xL/xH и yL/yH у GS v 0; без потолка
/// `(bmp.WidthBytes & 0xFF)` в эмиттере тихо теряет старшие биты, и на бумагу
/// уходит не то число, которое просили. Произведение двух валидных 65535
/// переполняет int, поэтому сравнение — в long.
///
/// Проверка стоит И в инициализаторе свойства (следит за конструктором: там
/// все три параметра доступны разом, порядок объявления полей роли не
/// играет), И в init-аксессоре (следит за `with`, который инициализаторы
/// вообще не перевызывает — копирует объект и лишь переприсваивает указанные
/// свойства через их init). Без второй проверки ровно то, что нашло ревью:
/// `bitmapOp with { Raster = new byte[1] }` и `with { WidthBytes = 9999 }`
/// тихо принимались бы — конструктор уже отработал, инициализатор больше не
/// выполняется, а тривиальный init без переопределения просто кладёт
/// значение как есть.
///
/// Известный компромисс: `with`, меняющий НЕСКОЛЬКО из трёх полей ОДНИМ
/// выражением на новый, но взаимно согласованный набор — например
/// `with { Raster = new byte[8], WidthBytes = 2, Height = 4 }` — может
/// упасть на промежуточном состоянии (первое изменённое поле проверяется
/// против ещё не обновлённых соседей). Стандартная плата за проверку в
/// каждом init независимо, а не единой проверкой после конструктора; сегодня
/// не задета — `BitmapOp` никто не конструирует, а обе дыры из ревью были
/// одиночными заменами ОДНОГО поля, которые это не касается.
/// Сегодня этот конструктор не вызывает никто (растровый логотип подключается
/// в Task 9), но заряженная сейчас мина по-прежнему мина.</summary>
public sealed record BitmapOp(byte[] Raster, int WidthBytes, int Height) : ReceiptOp
{
    private const int MaxDimension = 65535;

    public byte[] Raster
    {
        get;
        init => field = ValidateRaster(value, WidthBytes, Height);
    } = ValidateRaster(Raster, WidthBytes, Height);

    public int WidthBytes
    {
        get;
        init => field = ValidateDimension(value, nameof(WidthBytes), Height, Raster, isWidth: true);
    } = ValidateDimension(WidthBytes, nameof(WidthBytes), Height, Raster, isWidth: true);

    public int Height
    {
        get;
        init => field = ValidateDimension(value, nameof(Height), WidthBytes, Raster, isWidth: false);
    } = ValidateDimension(Height, nameof(Height), WidthBytes, Raster, isWidth: false);

    /// <summary>Диагностика на пути, который сегодня никто не вызывает
    /// (BitmapOp не конструирует ни один боевой код), но раз уж инвариант
    /// здесь — пусть сообщения не вводят в заблуждение. Три правки:
    /// null даёт ArgumentNullException, а не NullReferenceException из
    /// raster.LongLength; диапазон WidthBytes/Height проверяется РАНЬШЕ
    /// сравнения длины — без этого отрицательная ширина (пока её
    /// собственный init/инициализатор ещё не отработал: Raster идёт первым
    /// параметром и проверяется первым) давала бы невнятное "нужно ровно
    /// −4" вместо понятного "вне диапазона"; оба вызова ValidateDimension
    /// передают конкретное имя свойства, а не общее "value" параметра
    /// самого метода.</summary>
    private static byte[] ValidateRaster(byte[] raster, int widthBytes, int height)
    {
        if (raster is null) throw new ArgumentNullException(nameof(Raster));

        EnsureDimensionInRange(widthBytes, nameof(WidthBytes));
        EnsureDimensionInRange(height, nameof(Height));

        var expected = (long)widthBytes * height;
        if (raster.LongLength != expected)
            throw new ArgumentException(
                $"Растр логотипа не сходится с объявленным размером: {raster.Length} байт, " +
                $"WidthBytes={widthBytes}, Height={height} (нужно ровно {expected}).",
                nameof(Raster));
        return raster;
    }

    private static int ValidateDimension(int value, string paramName, int otherDimension, byte[] raster, bool isWidth)
    {
        EnsureDimensionInRange(value, paramName);

        var expected = isWidth ? (long)value * otherDimension : (long)otherDimension * value;
        if (raster.LongLength != expected)
            throw new ArgumentException(
                $"Растр логотипа не сходится с объявленным размером: {raster.Length} байт, " +
                $"нужно ровно {expected}.");

        return value;
    }

    private static void EnsureDimensionInRange(int value, string paramName)
    {
        if (value < 0 || value > MaxDimension)
            throw new ArgumentOutOfRangeException(paramName, value,
                $"Размер вне диапазона GS v 0 (0..{MaxDimension}).");
    }
}
