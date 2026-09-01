using System;
using System.IO.Ports;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Hardware;

/// <summary>Табло покупателя на последовательном порту.
///
/// Владеет портом и очередью отправок; какие именно байты уходят, решает
/// IDisplayProtocol. Разделение появилось из-за того, что порт на кассе один, а
/// диалектов у табло много: транспорт с его таймаутом и обработкой ошибок обязан
/// остаться в одном экземпляре, иначе исправление в нём теряется в одной из копий.</summary>
public class VfdDisplayService : ICustomerDisplayService
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly EscPosCodePage _codePage;
    private readonly IDisplayProtocol _protocol;
    private readonly SerialFraming _framing;
    private readonly bool _dtrRts;

    /// <summary>Параметры этого экземпляра — только для теста, по образцу
    /// CompositePrinterService.Printers: иначе строка, которая доносит их из настроек
    /// до железа, не покрыта ничем, и подмена конструктора на захардкоженные значения
    /// прошла бы мимо всех тестов незамеченной.</summary>
    internal string PortName => _portName;
    internal int BaudRate => _baudRate;
    internal EscPosCodePage CodePage => _codePage;
    internal IDisplayProtocol Protocol => _protocol;
    internal SerialFraming Framing => _framing;
    internal bool DtrRts => _dtrRts;

    /// <summary>Отправки выстраиваются в цепочку, а не идут параллельно. Task.Run
    /// снял блокировку UI-потока — и вместе с ней неявную сериализацию, на которой
    /// держались неожидаемые вызовы: одно нажатие «очистить корзину» поднимает
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

    /// <summary>Последние три параметра необязательные, и это не приглашение их не
    /// указывать: их умолчания в точности повторяют поведение кассы до появления
    /// протоколов, поэтому вызов, которому нечего про них сказать, ничего и не
    /// меняет. Что настройки действительно доезжают до железа, стережёт
    /// ConfiguredCustomerDisplayService и его тест, а не эти умолчания.</summary>
    public VfdDisplayService(
        string portName,
        int baudRate,
        EscPosCodePage codePage,
        IDisplayProtocol? protocol = null,
        SerialFraming? framing = null,
        bool dtrRts = false)
    {
        _portName = portName;
        _baudRate = baudRate;
        _codePage = codePage;
        _protocol = protocol ?? DisplayProtocols.Default;
        _framing = framing ?? SerialFramings.Default;
        _dtrRts = dtrRts;
    }

    public Task<bool> ShowLineAsync(string line1, string line2)
        => SendAsync(_protocol.BuildLine(line1, line2, _codePage));

    public Task<bool> ShowItemAsync(string name, decimal total)
        => SendAsync(_protocol.BuildItem(name, total, _codePage));

    public Task<bool> ShowTotalAsync(decimal total)
        => SendAsync(_protocol.BuildTotal(total, _codePage));

    public Task<bool> ClearAsync() => SendAsync(_protocol.BuildClear(_codePage));

    /// <summary>Кадр автоподбора. Не в ICustomerDisplayService: он нужен одному экрану
    /// настроек, а продаже — никогда, и место ему на конкретном классе, а не в
    /// контракте, который реализуют ещё и Null с Configured.</summary>
    public Task<bool> ShowProbeAsync(int number) => SendAsync(_protocol.BuildProbe(number));

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
    private Task<bool> SendAsync(byte[] frame)
    {
        lock (_queueGate)
        {
            var queued = _tail.ContinueWith(
                _ => SendNowAsync(frame),
                TaskScheduler.Default).Unwrap();

            _tail = queued;
            return queued;
        }
    }

    private async Task<bool> SendNowAsync(byte[] frame)
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
            //
            // DtrEnable/RtsEnable ставятся до Open(): часть табло без поднятых
            // линий данные не принимает, а некоторые от них ещё и питаются.
            using var port = new SerialPort(
                _portName, _baudRate, _framing.Parity, _framing.DataBits, _framing.StopBits)
            {
                WriteTimeout = 500,
                DtrEnable = _dtrRts,
                RtsEnable = _dtrRts,
            };
            port.Open();

            await port.BaseStream.WriteAsync(frame, 0, frame.Length);
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
}
