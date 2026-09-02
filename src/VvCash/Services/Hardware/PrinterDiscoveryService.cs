using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Runtime.Versioning;

namespace VvCash.Services.Hardware;

public static class PrinterDiscoveryService
{
    public static List<string> GetComPorts()
    {
        try
        {
            return SerialPort.GetPortNames().ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>Имена принтеров, которые можно выбрать на экране настроек.
    ///
    /// На Windows — очереди спулера через <see cref="WindowsRawPrinter.Enumerate"/>:
    /// перечисление живёт там же, где OpenPrinter, которому это имя потом
    /// отдаётся, и там же расписано, почему это больше не powershell с WMI.
    /// На остальных системах — lpstat из CUPS, как и было.
    ///
    /// Отказ обнаружения — пустой список, а не исключение: экран настроек обязан
    /// открыться и на машине без работающего спулера, пусть и с пустым
    /// выпадающим списком.</summary>
    public static List<string> GetUsbPrinters()
    {
        try
        {
            var printers = OperatingSystem.IsWindows()
                ? EnumerateSpoolerQueues()
                : GetCupsPrinters();

            return printers.Distinct().OrderBy(p => p).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PrinterDiscoveryService] Error discovering printers: {ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>Отдельным методом с атрибутом, а не вызовом на месте: проект
    /// таргетит net10.0, а не net10.0-windows, поэтому платформенная проверка
    /// обязательна, и OperatingSystem.IsWindows() — та форма, которую CA1416
    /// распознаёт как guard гарантированно. Тот же приём и по той же причине,
    /// что EscPosPrinterService.SendViaSpoolerAsync.</summary>
    [SupportedOSPlatform("windows")]
    private static List<string> EnumerateSpoolerQueues() => WindowsRawPrinter.Enumerate();

    private static List<string> GetCupsPrinters()
    {
        var printers = new List<string>();

        var processInfo = new ProcessStartInfo
        {
            FileName = "lpstat",
            Arguments = "-p",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processInfo);
        if (process == null) return printers;

        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("printer "))
            {
                var parts = line.Split(' ');
                if (parts.Length > 1)
                {
                    printers.Add(parts[1]);
                }
            }
        }

        return printers;
    }
}
