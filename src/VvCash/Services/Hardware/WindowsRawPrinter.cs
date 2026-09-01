using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VvCash.Services.Hardware;

/// <summary>Сырой поток байт в очередь спулера Windows.
///
/// Отдельно от EscPosPrinterService намеренно: тот про ESC/POS, а не про
/// маршалинг. Перечисление очередей (<see cref="Enumerate"/>) живёт здесь же,
/// а не в PrinterDiscoveryService, ровно ради того, чтобы имя, которое кассир
/// видит в настройках, и имя, которое получает OpenPrinter, приходили из одной
/// и той же winspool.drv — см. remarks у Enumerate.
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

            pageStarted = false;
            if (!EndPagePrinter(handle)) throw Failure("EndPagePrinter");

            docStarted = false;
            if (!EndDocPrinter(handle)) throw Failure("EndDocPrinter");
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeCoTaskMem(buffer);
            // Флаг сброшен перед своим End*-вызовом, а не после: сюда он доходит
            // взведённым только если до вызова дело не дошло вовсе. Повтор уже
            // отказавшего вызова — не уборка, а шум.
            // Отказы здесь игнорируются сознательно: бросок из finally затёр бы
            // исходное исключение, то есть настоящую причину.
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterInfo4
    {
        public IntPtr PrinterName;
        public IntPtr ServerName;
        public uint Attributes;
    }

    // LOCAL — принтеры, установленные на самой машине; CONNECTIONS — сетевые
    // подключения текущего пользователя (\сервер\принтер). Нужны оба: с одним
    // LOCAL сетевые принтеры пропадают из списка, а на точке их подключают
    // именно так не реже, чем локально.
    private const uint PrinterEnumLocal = 0x00000002;
    private const uint PrinterEnumConnections = 0x00000004;

    private const int ErrorInsufficientBuffer = 122;

    // Уровень 4 — самый дешёвый для перечисления: имя, сервер и флаги, без
    // обращения к драйверу за настройками каждого принтера.
    private const uint PrinterInfoLevel4 = 4;

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumPrinters(uint flags, string? name, uint level,
        IntPtr buffer, uint bufferBytes, out uint needed, out uint returned);

    /// <summary>Имена очередей спулера — то, что кассир выбирает в настройках и
    /// что затем уезжает в <see cref="Send"/>.
    ///
    /// Через winspool, а не через powershell.exe + WMI Win32_Printer, как было
    /// раньше. Скорость тут четвёртая причина, хотя симптомом была именно она;
    /// первые три важнее:
    ///
    ///  - Имя отсюда попадает в OpenPrinterW строкой в строку: обе функции живут
    ///    в одной winspool.drv и говорят UTF-16. Вывод powershell.exe проходил
    ///    через кодовую страницу консоли, и кириллическое имя приезжало
    ///    покорёженным — то есть таким, которое OpenPrinter уже не открывает.
    ///  - Нет зависимости от WMI: на кассовой машине с битым репозиторием WMI
    ///    запрос отдавал пустой список принтеров молча.
    ///  - Нет зависимости от powershell.exe: ExecutionPolicy, Constrained
    ///    Language Mode, AppLocker или антивирус на залоченной по GPO машине —
    ///    любое из этого давало тот же молчаливый пустой список.
    ///
    /// Скорость: холодный старт powershell с запросом к WMI занимал секунды, а
    /// зовётся это с UI-потока — SettingsViewModel.UpdateAvailableConnections
    /// вызывается на каждый принтер в конструкторе экрана настроек и на каждую
    /// смену типа подключения в выпадающем списке. Экран настроек и выбор USB
    /// замерзали на всё это время.
    ///
    /// Бросает при отказе спулера, как и <see cref="Send"/>; вызывающий
    /// (PrinterDiscoveryService) ловит и отдаёт пустой список.</summary>
    public static List<string> Enumerate()
    {
        const uint flags = PrinterEnumLocal | PrinterEnumConnections;

        // Первый вызов — только за размером буфера. Успех на нулевом буфере
        // означает, что перечислять нечего вовсе: принтеров в системе нет.
        if (EnumPrinters(flags, null, PrinterInfoLevel4, IntPtr.Zero, 0, out var needed, out _))
        {
            return new List<string>();
        }

        // Единственный ожидаемый отказ здесь. Любой другой — настоящая проблема
        // спулера, и молчать о ней значит вернуться к пустому списку без причины.
        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) throw Failure("EnumPrinters(size)");
        if (needed == 0) return new List<string>();

        var buffer = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            if (!EnumPrinters(flags, null, PrinterInfoLevel4, buffer, needed, out _, out var returned))
            {
                throw Failure("EnumPrinters");
            }

            var names = new List<string>(checked((int)returned));
            var stride = Marshal.SizeOf<PrinterInfo4>();
            for (var i = 0; i < returned; i++)
            {
                var info = Marshal.PtrToStructure<PrinterInfo4>(IntPtr.Add(buffer, i * stride));
                // Строки лежат в хвосте того же буфера, а не отдельными
                // выделениями: PtrToStringUni снимает копию, пока буфер жив, и
                // освобождать их по отдельности нечего и нельзя.
                var name = Marshal.PtrToStringUni(info.PrinterName);
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }

            return names;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
