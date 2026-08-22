using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

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

    public static List<string> GetUsbPrinters()
    {
        var printers = new List<string>();

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    // [Console]::OutputEncoding внутри самой команды, а не только
                    // StandardOutputEncoding снаружи: Windows PowerShell кодирует
                    // вывод раньше, чем его читает .NET, и одного ProcessStartInfo
                    // мало. Пока USB был заглушкой, покорёженное кириллическое имя
                    // никого не задевало — теперь на его побайтовой точности
                    // держится OpenPrinter.
                    Arguments = "-NoProfile -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; "
                              + "Get-WmiObject -Query 'SELECT Name FROM Win32_Printer' | Select-Object -ExpandProperty Name\"",
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                if (process != null)
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    printers.AddRange(lines.Select(l => l.Trim()));
                }
            }
            else // Linux / macOS
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = "lpstat",
                    Arguments = "-p",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                if (process != null)
                {
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
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error discovering printers: {ex.Message}");
        }

        return printers.Distinct().OrderBy(p => p).ToList();
    }
}
