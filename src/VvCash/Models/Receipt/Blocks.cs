using System;
using System.Collections.Generic;
using System.Linq;

namespace VvCash.Models.Receipt;

/// <summary>Строка свободного текста. Подстановки — плоские имена в фигурных
/// скобках, без циклов и условий; цикл по товарам делает ItemsBlock.</summary>
public sealed class TextBlock : ReceiptBlock
{
    /// <summary>Перевод строки заменяется пробелом: TextOp прямо запрещает его
    /// в своей строке — он прошёл бы эмиттер насквозь и дал бы на бумаге две
    /// строки мимо всей логики ширины и без пролога/эпилога атрибутов, которые
    /// рендерер ставит вокруг ровно одной строки на блок.</summary>
    private string _content = string.Empty;

    public string Content
    {
        get => _content;
        set => _content = (value ?? string.Empty).Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
    }

    public bool Bold { get; set; }
    public bool DoubleSize { get; set; }
}

/// <summary>Разделитель. Count = 0 означает «во всю ширину ленты» — это
/// классовый дефолт сам по себе; дефолтный шаблон ставит 28 явно в
/// ReceiptTemplate.Default, потому что столько дефисов печатает нынешний чек,
/// а замок совместимости считает байты.</summary>
public sealed class LineBlock : ReceiptBlock
{
    /// <summary>Пустая строка уронила бы рендер на первом же символе
    /// (IndexOutOfRangeException) — разбор JSON не единственный вход, тот же
    /// довод, что у ReceiptTemplate.Width. Управляющие символы (перевод строки
    /// среди них) отбрасываются, а не берутся как есть: рендерер и так режет
    /// строку до первого символа (l.Char.Substring(0, 1)), но делает это на
    /// сырых char, и "\n" первым символом дал бы на бумаге лишний перевод
    /// строки в обход всей логики ширины — тот же класс беды, что TextOp
    /// прямо запрещает в своей строке. Кламп здесь, а не только в рендерере:
    /// разбор — не единственный вход, и будущий генератор превью в бэкофисе
    /// читает это же свойство напрямую.</summary>
    private string _char = "-";

    public string Char
    {
        get => _char;
        set
        {
            var cleaned = new string((value ?? string.Empty).Where(c => !char.IsControl(c)).ToArray());
            _char = cleaned.Length > 0 ? cleaned.Substring(0, 1) : "-";
        }
    }

    /// <summary>Отрицательное число уронило бы рендер (повторяемая строка не
    /// принимает отрицательную длину), а огромное — ленту и память. Потолок —
    /// 200, вдвое больше самой широкой из поддерживаемых лент.
    ///
    /// Кламп Char молчит, потому что подмена там очевидна на глаз по самому
    /// чеку — не тот символ виден сразу. У Width тот же довод раньше
    /// оправдывал молчание, но с собственным верхним потолком её сеттер стал
    /// писать в лог при любом клампе, включая непозитивный вход (0, -5 → 32),
    /// который раньше проходил тихо. Здесь, у Count, довод другой:
    /// {"count":300} тихо становится 200, и ничего на бумаге не укажет, что
    /// значение вообще было отвергнуто, а не выбрано таким намеренно — потому
    /// лог нужен с самого начала.</summary>
    private const int MaxCount = 200;

    private int _count;

    public int Count
    {
        get => _count;
        set
        {
            var clamped = value < 0 ? 0 : Math.Min(value, MaxCount);
            if (clamped != value)
                Console.WriteLine($"[LineBlock] Count {value} вне диапазона [0, {MaxCount}], использую {clamped}");
            _count = clamped;
        }
    }
}

public sealed class FeedBlock : ReceiptBlock
{
    /// <summary>Отрицательное число строк подачи бессмысленно и уронило бы
    /// рендер тем же способом, что и отрицательный Count у LineBlock.
    /// Верхний потолок — тот же, что у Count (200): EscPosEmitter пишет
    /// перевод строки в цикле по Lines, и "lines":2000000000 из мусорного
    /// конфига — это не косметика, а два гигабайта в MemoryStream и
    /// OutOfMemoryException посреди чека.</summary>
    private const int MaxLines = 200;

    private int _lines = 1;

    public int Lines
    {
        get => _lines;
        set => _lines = value < 0 ? 0 : Math.Min(value, MaxLines);
    }
}

/// <summary>Одно поле реквизитов: что подставить и что написать перед ним.
/// Label — именно префикс, а не подпись с двоеточием от себя: нынешний чек
/// печатает "Doc #A-42" без пробела и дату вовсе без подписи.
///
/// Key/Label клампятся в сеттере тем же доводом, что и ReceiptTemplate.Width:
/// разбор — не единственный вход. "key":null — законный JSON, а не гипотеза:
/// сервер на Go сериализует так нулевую строку в структуре ничуть не реже,
/// чем nil-слайс для "blocks":null, от которого уже защищается
/// ReceiptTemplate.Parse. Без клампа здесь null долетал бы до
/// values.TryGetValue(field.Key, ...) в рендерере и ронял бы ArgumentNullException
/// на КАЖДОЙ продаже, пока кто-то не поправит шаблон — то есть чек не выходил
/// бы вовсе, ровно то, от чего защищается остальная часть этой фичи.</summary>
public sealed class ReceiptField
{
    private string _key = string.Empty;
    private string _label = string.Empty;

    public string Key
    {
        get => _key;
        set => _key = value ?? string.Empty;
    }

    public string Label
    {
        get => _label;
        set => _label = value ?? string.Empty;
    }
}

public sealed class FieldsBlock : ReceiptBlock
{
    /// <summary>"fields":null — то же самое "blocks":null у ReceiptTemplate,
    /// только на уровень ниже: nil-слайс, который json.Marshal на сервере
    /// пишет как literal null, а не "[]". Без клампа рендерер упал бы на
    /// foreach по null-списку — NullReferenceException на каждой продаже.</summary>
    private List<ReceiptField> _fields = new();

    public List<ReceiptField> Fields
    {
        get => _fields;
        set => _fields = value ?? new();
    }
}

public sealed class ItemsBlock : ReceiptBlock
{
    public bool ShowUnitPrice { get; set; }
    public bool ShowSku { get; set; }
    public bool ShowBarcode { get; set; }
    public bool ShowSecondaryUnit { get; set; } = true;
    public bool ShowLineDiscount { get; set; }

    /// <summary>Подпись строки скидки позиции. Настраиваемая, как
    /// TotalsBlock.DiscountLabel рядом — иначе один и тот же чек нёс бы два
    /// разных слова для одного смысла: перевод из локали на итогах и зашитую
    /// латиницу на самой позиции, непереводимую тем же конструктором, что
    /// собирает остальной шаблон.</summary>
    public string LineDiscountLabel { get; set; } = "Discount:";
}

public sealed class TotalsBlock : ReceiptBlock
{
    public bool ShowSubtotal { get; set; } = true;
    public string SubtotalLabel { get; set; } = "Subtotal:";
    public bool ShowDiscount { get; set; } = true;
    public string DiscountLabel { get; set; } = "Discount:";
    public bool ShowDiscountName { get; set; } = true;
    public string TotalLabel { get; set; } = "TOTAL:";
    public bool BoldTotal { get; set; } = true;
}

/// <remarks>Модель есть, печати ещё нет: у рендерера, который придёт следующей
/// задачей, пока нет операции печати QR-кода для EscPosEmitter.</remarks>
public sealed class QrBlock : ReceiptBlock
{
    public string Data { get; set; } = string.Empty;

    private int _moduleSize = 6;

    public int ModuleSize
    {
        get => _moduleSize;
        set => _moduleSize = value > 0 ? value : 6;
    }
}

public enum BarcodeSymbology
{
    Code128,
    Ean13,
}

/// <remarks>Модель есть, печати ещё нет: у рендерера, который придёт следующей
/// задачей, пока нет операции печати штрихкода для EscPosEmitter.</remarks>
public sealed class BarcodeBlock : ReceiptBlock
{
    public string Data { get; set; } = string.Empty;
    public BarcodeSymbology Symbology { get; set; } = BarcodeSymbology.Code128;

    private int _height = 64;

    public int Height
    {
        get => _height;
        set => _height = value > 0 ? value : 64;
    }

    public bool PrintHri { get; set; } = true;
}

public enum LogoSource
{
    /// <summary>Логотип уже прошит в память принтера; чек печатает слот.</summary>
    Nv,
    /// <summary>Растр приезжает отдельной опцией конфига receipt_logo.</summary>
    Bitmap,
}

/// <remarks>Модель есть, печати ещё нет: у рендерера, который придёт следующей
/// задачей, пока нет операции печати логотипа для EscPosEmitter.</remarks>
public sealed class LogoBlock : ReceiptBlock
{
    public LogoSource Source { get; set; } = LogoSource.Nv;

    private int _nvSlot = 1;

    public int NvSlot
    {
        get => _nvSlot;
        set => _nvSlot = value > 0 ? value : 1;
    }
}
