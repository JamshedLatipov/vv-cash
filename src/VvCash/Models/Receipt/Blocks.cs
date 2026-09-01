using System;
using System.Collections.Generic;

namespace VvCash.Models.Receipt;

/// <summary>Строка свободного текста. Подстановки — плоские имена в фигурных
/// скобках, без циклов и условий; цикл по товарам делает ItemsBlock.</summary>
public sealed class TextBlock : ReceiptBlock
{
    public string Content { get; set; } = string.Empty;
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
    /// довод, что у ReceiptTemplate.Width.</summary>
    private string _char = "-";

    public string Char
    {
        get => _char;
        set => _char = string.IsNullOrEmpty(value) ? "-" : value;
    }

    /// <summary>Отрицательное число уронило бы рендер (повторяемая строка не
    /// принимает отрицательную длину), а огромное — ленту и память. Потолок —
    /// 200, вдвое больше самой широкой из поддерживаемых лент.</summary>
    private const int MaxCount = 200;

    private int _count;

    public int Count
    {
        get => _count;
        set => _count = value < 0 ? 0 : Math.Min(value, MaxCount);
    }
}

public sealed class FeedBlock : ReceiptBlock
{
    /// <summary>Отрицательное число строк подачи бессмысленно и уронило бы
    /// рендер тем же способом, что и отрицательный Count у LineBlock.</summary>
    private int _lines = 1;

    public int Lines
    {
        get => _lines;
        set => _lines = value < 0 ? 0 : value;
    }
}

/// <summary>Одно поле реквизитов: что подставить и что написать перед ним.
/// Label — именно префикс, а не подпись с двоеточием от себя: нынешний чек
/// печатает "Doc #A-42" без пробела и дату вовсе без подписи.</summary>
public sealed class ReceiptField
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public sealed class FieldsBlock : ReceiptBlock
{
    public List<ReceiptField> Fields { get; set; } = new();
}

public sealed class ItemsBlock : ReceiptBlock
{
    public bool ShowUnitPrice { get; set; }
    public bool ShowSku { get; set; }
    public bool ShowBarcode { get; set; }
    public bool ShowSecondaryUnit { get; set; } = true;
    public bool ShowLineDiscount { get; set; }
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
