using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VvCash.Services.Hardware;

/// <summary>Сырой поток байт в очередь спулера Windows.
///
/// Отдельно от EscPosPrinterService намеренно: тот про ESC/POS, а не про
/// маршалинг. Имя очереди — то же, что перечисляет PrinterDiscoveryService,
/// то есть ровно то, что кассир выбрал в настройках.
///
/// Каждый вызов winspool возвращает bool. Игнорировать их — воспроизвести на
/// новом уровне ту самую ложь, ради которой файл написан.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsRawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);

    // Возвращает DWORD — идентификатор задания, не BOOL. Ненулевой при успехе,
    // ноль при отказе, поэтому маршалинг в bool корректен; так же объявлено в
    // образце RawPrinterHelper у Microsoft.
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartDocPrinter(IntPtr handle, int level, ref DocInfo1 info);

    [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr handle, IntPtr bytes, int count, out int written);

    /// <summary>Бросает при любом отказе спулера. Вызывающий (EscPosPrinterService)
    /// ловит и выставляет PrinterStatus.Error.</summary>
    public static void Send(string printerName, byte[] data)
    {
        // Пустое задание встало бы в очередь и отчиталось успехом: WritePrinter
        // пишет ноль байт без ошибки, и проверка короткой записи даёт 0 != 0.
        // Сегодня недостижимо — любой построитель шлёт минимум ESC @, — но
        // опираться на это молча не стоит.
        if (data.Length == 0) throw new ArgumentException("Nothing to print.", nameof(data));

        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero))
        {
            throw Failure($"OpenPrinter('{printerName}')");
        }

        var buffer = IntPtr.Zero;
        var docStarted = false;
        var pageStarted = false;
        try
        {
            // RAW — без него спулер отдал бы байты драйверу как документ на
            // отрисовку, и ESC/POS до принтера не доехал бы.
            var info = new DocInfo1 { DocName = "VvCash receipt", OutputFile = null, DataType = "RAW" };
            if (!StartDocPrinter(handle, 1, ref info)) throw Failure("StartDocPrinter");
            docStarted = true;

            if (!StartPagePrinter(handle)) throw Failure("StartPagePrinter");
            pageStarted = true;

            buffer = Marshal.AllocCoTaskMem(data.Length);
            Marshal.Copy(data, 0, buffer, data.Length);

            if (!WritePrinter(handle, buffer, data.Length, out var written))
            {
                throw Failure("WritePrinter");
            }

            // Короткая запись не считается отказом на уровне API, но чек при ней
            // выходит обрезанным — а обрезанный чек это тот же молчаливый успех.
            // Обрезанное задание при этом всё равно зафиксируется в очереди: из
            // принтера выйдет половина чека, а кассир прочитает отказ. Это
            // сознательный размен — потерять половину чека лучше, чем считать
            // напечатанным то, что напечаталось не полностью.
            if (written != data.Length)
            {
                throw new InvalidOperationException(
                    $"WritePrinter accepted {written} of {data.Length} bytes.");
            }

            if (!EndPagePrinter(handle)) throw Failure("EndPagePrinter");
            pageStarted = false;

            if (!EndDocPrinter(handle)) throw Failure("EndDocPrinter");
            docStarted = false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeCoTaskMem(buffer);
            // Взведёнными флаги доходят сюда только при раскрутке исключения.
            // Здесь отказы игнорируются сознательно: бросок из finally затёр бы
            // исходное исключение, то есть настоящую причину. На успешном пути
            // оба End* уже вызваны выше и проверены — иначе незафиксированное
            // задание молча возвращало бы успех.
            if (pageStarted) EndPagePrinter(handle);
            if (docStarted) EndDocPrinter(handle);
            // ClosePrinter игнорируется всегда: его отказ уже ничего не отменяет.
            ClosePrinter(handle);
        }
    }

    private static InvalidOperationException Failure(string call)
    {
        // Снимается до создания Win32Exception: её конструктор сам может
        // затереть последнюю ошибку потока.
        var code = Marshal.GetLastWin32Error();
        return new InvalidOperationException(
            $"{call} failed: {new Win32Exception(code).Message} (Win32 {code}).");
    }
}
