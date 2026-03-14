using System;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;

namespace VvCash.Services.Hardware;

public class VfdDisplayService : ICustomerDisplayService
{
    private readonly string _portName;

    public VfdDisplayService(string portName)
    {
        _portName = portName;
    }

    public Task ShowLineAsync(string line1, string line2) => SendAsync(FormatLine(line1, 20) + FormatLine(line2, 20));
    public Task ShowItemAsync(string name, decimal price) => ShowLineAsync(name, $"${price:F2}");
    public Task ShowTotalAsync(decimal total) => ShowLineAsync("TOTAL", $"${total:F2}");
    public Task ClearAsync() => SendAsync(new string(' ', 40));

    private async Task SendAsync(string text)
    {
        try
        {
            using var port = new SerialPort(_portName, 9600);
            port.Open();
            var bytes = Encoding.ASCII.GetBytes(text);
            await port.BaseStream.WriteAsync(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VFD error: {ex.Message}");
        }
    }

    private static string FormatLine(string text, int width)
    {
        if (text.Length >= width) return text[..width];
        return text.PadRight(width);
    }
}
