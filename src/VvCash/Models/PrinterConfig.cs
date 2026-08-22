namespace VvCash.Models;

public class PrinterConfig
{
    public string Name { get; set; } = string.Empty;
    public PrinterConnectionType ConnectionType { get; set; }
    public string ConnectionString { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;

    /// <summary>Id записи из EscPosCodePages. На принтер, а не на кассу: в магазине
    /// могут стоять две разные железки. Пусто на конфигурации, где настройку не
    /// трогали; Resolve читает пустое и незнакомое как CP866, поэтому обновление
    /// существующей кассы ничего не меняет.</summary>
    public string CodePageId { get; set; } = string.Empty;
}
