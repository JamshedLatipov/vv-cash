using System.Text.Json.Serialization;

namespace VvCash.Models.Receipt;

/// <summary>Один элемент чека. Порядок задаётся позицией в списке шаблона, а не
/// полем: список и есть порядок, и второй источник правды тут не нужен.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(LineBlock), "line")]
[JsonDerivedType(typeof(FeedBlock), "feed")]
[JsonDerivedType(typeof(FieldsBlock), "fields")]
[JsonDerivedType(typeof(ItemsBlock), "items")]
[JsonDerivedType(typeof(TotalsBlock), "totals")]
[JsonDerivedType(typeof(QrBlock), "qr")]
[JsonDerivedType(typeof(BarcodeBlock), "barcode")]
[JsonDerivedType(typeof(LogoBlock), "logo")]
public abstract class ReceiptBlock
{
    /// <summary>Выключенный блок остаётся в шаблоне и не печатается. Так гасят
    /// строку, не теряя её настройки — то же решение, что PrintRole.None у
    /// принтера против IsEnabled.</summary>
    public bool Enabled { get; set; } = true;

    public ReceiptAlign Align { get; set; } = ReceiptAlign.Left;
}
