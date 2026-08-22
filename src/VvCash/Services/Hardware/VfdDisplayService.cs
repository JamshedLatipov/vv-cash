using System;
using System.IO.Ports;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Hardware;

/// <summary>Двухстрочный VFD на последовательном порту.
///
/// Реализация сознательно консервативная. Инициализацию (ESC @) и выбор кодовой
/// страницы (ESC t n) понимают практически все VFD; команды позиционирования
/// курсора у моделей расходятся сильнее, чем у принтеров, поэтому их здесь нет —
/// 40 символов двумя строками по 20, и модель раскладывает их сама.</summary>
public class VfdDisplayService : ICustomerDisplayService
{
    private const int Columns = 20;

    private readonly string _portName;
    private readonly int _baudRate;
    private readonly EscPosCodePage _codePage;

    /// <summary>Последнее, что уходило на дисплей. Существует ради тестов: открыть
    /// настоящий COM-порт в юнит-тесте нельзя, а разметку и отсутствие доллара
    /// проверить надо.</summary>
    public string LastRendered { get; private set; } = string.Empty;

    public VfdDisplayService(string portName, int baudRate, EscPosCodePage codePage)
    {
        _portName = portName;
        _baudRate = baudRate;
        _codePage = codePage;
    }

    public Task<bool> ShowLineAsync(string line1, string line2)
        => SendAsync(Pad(line1) + Pad(line2));

    // Без валюты: символ был зашит в "$" на кассах, которые долларов не берут —
    // ровно то же, что уже чинили на чеке.
    public Task<bool> ShowItemAsync(string name, decimal price)
        => ShowLineAsync(name, Money(price));

    public Task<bool> ShowTotalAsync(decimal total)
        => ShowLineAsync("TOTAL", Money(total));

    public Task<bool> ClearAsync() => SendAsync(new string(' ', Columns * 2));

    private static string Money(decimal value)
        => value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<bool> SendAsync(string text)
    {
        LastRendered = text;

        try
        {
            using var port = new SerialPort(_portName, _baudRate);
            port.Open();

            // ESC @, затем ESC t n. Без инициализации дисплей копит мусор от
            // предыдущей строки; без кодовой страницы кириллица уходит в ASCII и
            // превращается в вопросительные знаки.
            var prologue = new byte[] { 0x1B, 0x40, 0x1B, 0x74, _codePage.EscTSelector };
            await port.BaseStream.WriteAsync(prologue, 0, prologue.Length);

            var bytes = _codePage.Encoding.GetBytes(text);
            await port.BaseStream.WriteAsync(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception ex)
        {
            // Логируется, но не глотается: возвращённый false — единственное, по
            // чему кнопка проверки отличит рабочий дисплей от мёртвого порта.
            Console.WriteLine($"VFD error: {ex.Message}");
            return false;
        }
    }

    private static string Pad(string text)
        => text.Length >= Columns ? text[..Columns] : text.PadRight(Columns);
}
