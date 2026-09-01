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

/// <summary>Разделитель. Count = 0 означает «во всю ширину ленты»; дефолтный
/// шаблон ставит 28 явно, потому что столько дефисов печатает нынешний чек, а
/// замок совместимости считает байты.</summary>
public sealed class LineBlock : ReceiptBlock
{
    public string Char { get; set; } = "-";
    public int Count { get; set; } = 28;
}

public sealed class FeedBlock : ReceiptBlock
{
    public int Lines { get; set; } = 1;
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

public sealed class QrBlock : ReceiptBlock
{
    public string Data { get; set; } = string.Empty;
    public int ModuleSize { get; set; } = 6;
}

public enum BarcodeSymbology
{
    Code128,
    Ean13,
}

public sealed class BarcodeBlock : ReceiptBlock
{
    public string Data { get; set; } = string.Empty;
    public BarcodeSymbology Symbology { get; set; } = BarcodeSymbology.Code128;
    public int Height { get; set; } = 64;
    public bool PrintHri { get; set; } = true;
}

public enum LogoSource
{
    /// <summary>Логотип уже прошит в память принтера; чек печатает слот.</summary>
    Nv,
    /// <summary>Растр приезжает отдельной опцией конфига receipt_logo.</summary>
    Bitmap,
}

public sealed class LogoBlock : ReceiptBlock
{
    public LogoSource Source { get; set; } = LogoSource.Nv;
    public int NvSlot { get; set; } = 1;
}
