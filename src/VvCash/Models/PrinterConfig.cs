namespace VvCash.Models;

public class PrinterConfig
{
    public string Name { get; set; } = string.Empty;
    public PrinterConnectionType ConnectionType { get; set; }
    public string ConnectionString { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}
