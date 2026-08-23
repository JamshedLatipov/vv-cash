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

    /// <summary>Порт, скорость и кодовая страница этого экземпляра — только для
    /// теста, по образцу CompositePrinterService.Printers: иначе строка, которая
    /// доносит их из настроек до железа, не покрыта ничем, и подмена конструктора
    /// на захардкоженные значения прошла бы мимо всех тестов незамеченной.</summary>
    internal string PortName => _portName;
    internal int BaudRate => _baudRate;
    internal EscPosCodePage CodePage => _codePage;

    /// <summary>Отправки выстраиваются в цепочку, а не идут параллельно. Task.Run
    /// снял блокировку UI-потока — и вместе с ней неявную сериализацию, на которой
    /// держались пять неожидаемых вызовов: одно нажатие «очистить корзину» поднимает
    /// CartChanged дважды, ClearCustomerDiscount третий раз, плюс ClearAsync — четыре
    /// одновременные отправки в один COM-порт. Второй поток получил бы
    /// UnauthorizedAccessException на Open(), кадр молча потерялся бы, и какой именно
    /// уцелеет — было бы неопределено.
    ///
    /// Цепочкой, а не семафором: у двухстрочного дисплея важен порядок кадров —
    /// «товар, затем итог» и «итог, затем спасибо» читаются покупателем как
    /// последовательность, а семафор такой гарантии не даёт.</summary>
    private readonly object _queueGate = new();
    private Task<bool> _tail = Task.FromResult(true);

    public VfdDisplayService(string portName, int baudRate, EscPosCodePage codePage)
    {
        _portName = portName;
        _baudRate = baudRate;
        _codePage = codePage;
    }

    /// <summary>Кадр отдельно от отправки, как Build*Receipt у принтера: разметку
    /// можно проверить, не открывая порт.</summary>
    public static string BuildFrame(string line1, string line2) => Pad(line1) + Pad(line2);

    // Без валюты: символ был зашит в "$" на кассах, которые долларов не берут —
    // ровно то же, что уже чинили на чеке.
    /// <summary>Кадр целиком, вместе с форматированием суммы. Отдельно от
    /// BuildFrame, потому что доллар жил именно здесь — в форматировании денег,
    /// а не в набивке колонок: BuildFrame по устройству не может подставить
    /// валюту, что бы Money ни делал, и тест против него ничего не сторожил бы.</summary>
    public static string BuildItemFrame(string name, decimal price) => BuildFrame(name, Money(price));

    public static string BuildTotalFrame(decimal total) => BuildFrame("TOTAL", Money(total));

    public Task<bool> ShowLineAsync(string line1, string line2)
        => SendAsync(BuildFrame(line1, line2));

    public Task<bool> ShowItemAsync(string name, decimal price) => SendAsync(BuildItemFrame(name, price));

    public Task<bool> ShowTotalAsync(decimal total) => SendAsync(BuildTotalFrame(total));

    public Task<bool> ClearAsync() => SendAsync(new string(' ', Columns * 2));

    private static string Money(decimal value)
        => value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Ставит отправку в хвост очереди и возвращает её задачу, не дожидаясь
    /// её здесь. Лок защищает только само связывание с хвостом — ни одного await
    /// внутри него, компилятор бы и не разрешил, — а ContinueWith на
    /// TaskScheduler.Default сам уводит SendNowAsync с потока вызывающего, даже
    /// когда _tail уже завершён к моменту вызова: без ExecuteSynchronously
    /// продолжение всегда планируется через переданный планировщик, а не
    /// выполняется на месте. Поэтому отдельный Task.Run больше не нужен.
    ///
    /// SendNowAsync никогда не бросает — см. её catch — поэтому _tail всегда
    /// оказывается в состоянии RanToCompletion, и упавшая отправка не может
    /// застрять сама и подвесить очередь для всех, кто встанет за ней.</summary>
    private Task<bool> SendAsync(string text)
    {
        lock (_queueGate)
        {
            var queued = _tail.ContinueWith(
                _ => SendNowAsync(text),
                TaskScheduler.Default).Unwrap();

            _tail = queued;
            return queued;
        }
    }

    private async Task<bool> SendNowAsync(string text)
    {
        try
        {
            // WriteTimeout: по умолчанию бесконечен, а порт может открыться и
            // при этом никогда не вычитать буфер — мёртвый, но ещё
            // перечисленный VFD, либо аппаратное управление потоком. Без этой
            // строки WriteAsync висит вечно, using port так и не отрабатывает,
            // дескриптор утекает на каждое сканирование, а бросить нечего —
            // catch тут не помощник. 40 байт на 9600 бод — это ~42мс на
            // проводе, 500мс — с большим запасом.
            using var port = new SerialPort(_portName, _baudRate) { WriteTimeout = 500 };
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
            // чему кнопка проверки отличит рабочий дисплей от мёртвого порта. Это
            // же catch — единственное, что держит _tail в RanToCompletion и не
            // даёт упавшему звену подвесить очередь.
            Console.WriteLine($"VFD error: {ex.Message}");
            return false;
        }
    }

    private static string Pad(string text)
        => text.Length >= Columns ? text[..Columns] : text.PadRight(Columns);
}
