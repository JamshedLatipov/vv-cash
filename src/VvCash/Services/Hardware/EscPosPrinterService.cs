using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Models.Receipt;
using VvCash.Services.Logging;
using VvCash.Services.Rendering;

namespace VvCash.Services.Hardware;

public class EscPosPrinterService : IPrinterService
{
    private readonly PrinterConnectionType _connectionType;
    private readonly string _connectionString;
    private readonly EscPosCodePage _codePage;
    private readonly PrintRole _roles;
    private PrinterStatus _status = PrinterStatus.Ready;

    public PrinterStatus Status => _status;
    public event EventHandler<PrinterStatus>? StatusChanged;

    /// <summary>Какая таблица реально применена к этому принтеру. Существует ради
    /// теста: строка, которая доносит настройку до боевой кассы, иначе не
    /// покрывается ничем, и её пропажу ловил бы только grep.</summary>
    public EscPosCodePage CodePage => _codePage;

    /// <summary>Какие документы печатает этот аппарат. Значение по умолчанию —
    /// Receipt: служба, собранная на экране настроек ради пробной печати, ролями
    /// не пользуется вовсе, и заставлять её их объявлять незачем.</summary>
    public PrintRole Roles => _roles;

    /// <summary>Бюджет на ОДНУ сетевую фазу SendViaLan — на попытку соединения и
    /// отдельно на саму запись, а не на обе разом: здоровое соединение занимает
    /// доли миллисекунды, и почти весь бюджет всё равно достаётся записи.
    ///
    /// Измерено на этой машине разовым скриптом на голых сокетах (числа — в
    /// сообщении коммита, который завёл этот таймаут; здесь не продублированы,
    /// чтобы поменять таймаут можно было не редактируя чужой протокол замера):
    /// - занятый порт на loopback (принтер выключен, порт слушает кто-то другой)
    ///   — ОС сама отказывает за ~2038 мс (SYN получает RST);
    /// - адрес, на который вообще никто не отвечает (принтер сменил IP, второй
    ///   адаптер, опечатка в настройках) — SYN не получает ни RST, ни ответа
    ///   вообще, и ОС досылает его по своим ретраям ~21058 мс, прежде чем
    ///   сдаться — на порядок хуже занятого порта, и именно это возместить
    ///   нечем, кроме своего таймаута;
    /// - живой слушатель на этой же машине — 0 мс.
    /// Секунда сети магазина, где принтер висит на том же коммутаторе, что
    /// касса, отвечает за миллисекунды или не отвечает вовсе — секунда кладёт
    /// между этими случаями запас на порядок, не отрезая по живому первый.
    ///
    /// internal set, а не readonly: EscPosLanTransportTest сокращает его на
    /// проверке подключения к закрытому порту — иначе тест либо ждёт секунду
    /// по-настоящему на каждый прогон, либо вовсе не отличает «отказала наша
    /// отмена» от «наконец ответила ОС» (см. комментарий самого теста).</summary>
    internal TimeSpan LanTimeout { get; set; } = TimeSpan.FromSeconds(1);

    private static readonly byte[] CmdInit = { 0x1B, 0x40 };
    private static readonly byte[] CmdSelectCodeTable = { 0x1B, 0x74 };
    /// <summary>FS . — выход из двухбайтового режима китайских иероглифов.
    ///
    /// Xprinter XP-80 приезжает с завода с включённым этим режимом, и пока он
    /// включён, ESC t принтером просто игнорируется, а каждый байт от 0x80 и выше
    /// читается как половина иероглифа. Кириллица в CP866 вся лежит выше 0x80,
    /// поэтому чек выходил иероглифами при ЛЮБОЙ выбранной таблице — настройка
    /// кодовой страницы на такой кассе не действовала вовсе. Проверено разовой
    /// развёрткой ESC t 0..50 на живом XP-80: без FS . читаемой кириллицы нет ни
    /// под одним селектором, с FS . она читается под 9, 17 и 23 в CP866.
    ///
    /// Шлётся всегда, а не под настройку: у принтера без китайского режима
    /// команда — no-op, а угадывать по модели нечего.</summary>
    private static readonly byte[] CmdCancelKanji = { 0x1C, 0x2E };
    private static readonly byte[] CmdAlignLeft = { 0x1B, 0x61, 0x00 };
    private static readonly byte[] CmdAlignCenter = { 0x1B, 0x61, 0x01 };
    private static readonly byte[] CmdAlignRight = { 0x1B, 0x61, 0x02 };
    private static readonly byte[] CmdBoldOn = { 0x1B, 0x45, 0x01 };
    private static readonly byte[] CmdBoldOff = { 0x1B, 0x45, 0x00 };
    private static readonly byte[] CmdDoubleSizeOn = { 0x1B, 0x21, 0x30 };
    private static readonly byte[] CmdDoubleSizeOff = { 0x1B, 0x21, 0x00 };
    private static readonly byte[] CmdCut = { 0x1D, 0x56, 0x42, 0x00 };
    private static readonly byte[] CmdLineFeed = { 0x0A };
    public static readonly byte[] CmdDrawerKick = { 0x1B, 0x70, 0x00, 0x19, 0xFA };

    public EscPosPrinterService(PrinterConnectionType connectionType, string connectionString,
        EscPosCodePage codePage, PrintRole roles = PrintRole.Receipt)
    {
        _connectionType = connectionType;
        _connectionString = connectionString;
        _codePage = codePage;
        _roles = roles;
    }

    /// <summary>Собирает байты чека продажи из десяти позиционных аргументов.
    /// Раскладка живёт в ReceiptRenderer, байты — в EscPosEmitter; эта
    /// перегрузка только складывает аргументы в SaleReceiptData и передаёт
    /// его перегрузке ниже. Она остаётся ради тестов и вызывающего кода,
    /// который ещё не перешёл на запись целиком — PrintReceiptAsync такой
    /// же намеренный долгожитель, что и BuildReturnReceipt рядом.
    ///
    /// template = null означает «шаблон с сервера не доехал» и берёт
    /// ReceiptTemplate.Default, который печатает ровно то, что печаталось до
    /// перевода на блоки. Это свойство закреплено SaleReceiptGoldenTest.</summary>
    public static byte[] BuildSaleReceipt(
        EscPosCodePage codePage,
        IEnumerable<CartItem> items, decimal subtotal, decimal discount, decimal total,
        string? discountName = null,
        string? documentNumber = null, string? warehouseName = null,
        string? sellerName = null, string? saleDate = null,
        string? queueNumber = null,
        ReceiptTemplate? template = null)
    {
        var sale = new SaleReceiptData(
            new List<CartItem>(items), subtotal, discount, total,
            discountName, documentNumber, warehouseName, sellerName, saleDate, queueNumber);

        return BuildSaleReceipt(codePage, sale, template);
    }

    /// <summary>Тот же чек, но из готовой записи — вход для вызывающего кода,
    /// у которого SaleReceiptData уже есть целиком, а не из десяти отдельных
    /// переменных. Заведена ради PrintKitchenOrderAsync: раньше он раскладывал
    /// свой sale на те же десять аргументов и звал перегрузку выше, которая
    /// тут же собирала из них новую такую же запись — круг, а не пропуск
    /// значения, и на этом круге терялось QueueNumber самой записи, потому
    /// что старый путь читал одноимённый позиционный параметр, а не поле
    /// sale.QueueNumber. Через эту перегрузку запись едет до Render один раз,
    /// без промежуточной пересборки.
    ///
    /// Чек продажи — единственный документ в этом файле, собранный так. Пять
    /// остальных (пречек, талон, чек возврата, чек обмена, пробный чек) всё
    /// ещё пишут байты сами, теми же WriteInit/Write/WriteLine и своими
    /// строковыми литералами, что и этот метод писал до перевода на шаблон.
    /// Это не недосмотр: план, который завёл ReceiptTemplate и рендерер, их
    /// нарочно не трогает (см. Task 13 в плане), и EscPosEmitter держит
    /// собственную копию CmdCancelKanji с комментарием об этом же дублировании.
    /// Так что асимметрия — текущая граница объёма этой работы, а не
    /// промежуточное состояние с известной датой закрытия; перевод остальных
    /// пяти — отдельная, пока не заведённая задача.</summary>
    public static byte[] BuildSaleReceipt(EscPosCodePage codePage, SaleReceiptData sale, ReceiptTemplate? template = null)
    {
        return EscPosEmitter.Emit(
            ReceiptRenderer.Render(template ?? ReceiptTemplate.Default, sale),
            codePage);
    }

    /// <summary>Образец, по которому на точке решают, угадана ли таблица.
    ///
    /// Не «Hello world»: проверять надо ровно то, что ломалось. Русская строка —
    /// собственно проверка; строка таджикских и казахских букв напечатается
    /// вопросительными знаками при ЛЮБОЙ записи каталога, и это ожидаемо —
    /// однобайтовой таблицы под них у ESC/POS нет. Она стоит здесь, чтобы это
    /// увидели на бумаге, а не на названиях товаров через неделю. Латиница и
    /// цифры отделяют «таблица не та» от «принтер вообще не тот». Казахская
    /// «і» из образца намеренно убрана: она единственная из этого ряда есть в
    /// CP1251, и на одной уцелевшей букве приёмка на точке спотыкалась бы о
    /// собственную инструкцию «должны быть одни вопросительные знаки».</summary>
    public static byte[] BuildTestReceipt(EscPosCodePage codePage)
    {
        using var ms = new MemoryStream();
        WriteInit(ms, codePage);
        Write(ms, CmdAlignCenter);
        Write(ms, CmdBoldOn);
        WriteLine(ms, "TEST / ПРОБНАЯ ПЕЧАТЬ", codePage);
        Write(ms, CmdBoldOff);
        WriteLine(ms, "----------------------------", codePage);
        Write(ms, CmdAlignLeft);
        WriteLine(ms, "RU: Ёжик съел 12 шт.", codePage);
        WriteLine(ms, "TJ/KK: ӯ ғ қ ҳ ҷ ә ң ө ұ ү", codePage);
        WriteLine(ms, "LAT: The quick brown fox", codePage);
        WriteLine(ms, "NUM: 0123456789", codePage);
        WriteLine(ms, "----------------------------", codePage);
        // Что именно пробовали — чтобы точка могла назвать это по телефону, не
        // залезая в настройки.
        WriteLine(ms, $"{codePage.Id}   ESC t {codePage.EscTSelector}", codePage);
        Write(ms, CmdLineFeed);
        Write(ms, CmdLineFeed);
        Write(ms, CmdCut);
        return ms.ToArray();
    }

    /// <summary>Отправляет <see cref="BuildTestReceipt"/> и не глотает отказ:
    /// кнопке проверки нужен не bool, а причина.
    ///
    /// SetStatus здесь намеренно нет, в отличие от пяти боевых методов. Служба
    /// для проверки строится из несохранённых значений с экрана настроек, на её
    /// StatusChanged никто не подписан — а если бы подписался, разовая
    /// диагностика перекрашивала бы индикатор готовности боевой кассы.</summary>
    public Task PrintTestReceiptAsync() => SendAsync(BuildTestReceipt(_codePage));

    public async Task<bool> PrintReceiptAsync(IEnumerable<CartItem> items, decimal subtotal, decimal discount, decimal total, IEnumerable<Coupon> coupons, string? discountName = null,
        string? documentNumber = null, string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        try
        {
            await SendAsync(BuildSaleReceipt(_codePage, items, subtotal, discount, total, discountName,
                documentNumber, warehouseName, sellerName, saleDate));
            SetStatus(PrinterStatus.Ready);
            return true;
        }
        catch (Exception ex)
        {
            var chain = AppLogging.DescribeChain(ex);
            Console.WriteLine($"Print error: {chain}");
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }

    /// <summary>Builds the pre-receipt bytes. Static and separate from sending,
    /// exactly as BuildSaleReceipt is. This was assembled inline in
    /// PrintPreReceiptAsync, which is why it was the one ESC @ site with no test
    /// of its own: the layout could only be reached through a socket.</summary>
    public static byte[] BuildPreReceipt(EscPosCodePage codePage, IEnumerable<CartItem> items, decimal total)
    {
        using var ms = new MemoryStream();
        WriteInit(ms, codePage);
        Write(ms, CmdAlignCenter);
        WriteLine(ms, "PRE-RECEIPT", codePage);
        WriteLine(ms, "----------------------------", codePage);
        Write(ms, CmdAlignLeft);
        foreach (var item in items)
        {
            WriteLine(ms, $"  {item.Product.Name} x{item.QuantityDisplay}", codePage);
            if (item.Product.HasSecondaryUnit)
                WriteLine(ms, $"    {item.QuantityInUnitDisplay} {item.Product.UnitShortName}", codePage);
        }
        WriteLine(ms, ReceiptText.PadLine("TOTAL:", ReceiptText.Money(total), 32), codePage);
        Write(ms, CmdLineFeed);
        Write(ms, CmdCut);
        return ms.ToArray();
    }

    /// <summary>Талон клиенту: номер и ничего лишнего. Отдельный документ, а не
    /// строка на чеке — клиент отдаёт талон, получая заказ, а чек оставляет себе.
    /// Время и точка печатаются, когда переданы: талон из кассы без склада в
    /// настройках не должен нести пустую строку.</summary>
    public static byte[] BuildTicket(EscPosCodePage codePage, string number,
        string? time = null, string? warehouseName = null)
    {
        using var ms = new MemoryStream();
        WriteInit(ms, codePage);
        Write(ms, CmdAlignCenter);
        WriteLine(ms, "----------------------------", codePage);
        Write(ms, CmdDoubleSizeOn);
        Write(ms, CmdBoldOn);
        WriteLine(ms, number, codePage);
        Write(ms, CmdBoldOff);
        Write(ms, CmdDoubleSizeOff);
        WriteLine(ms, "----------------------------", codePage);
        if (!string.IsNullOrWhiteSpace(warehouseName)) WriteLine(ms, warehouseName!, codePage);
        if (!string.IsNullOrWhiteSpace(time)) WriteLine(ms, time!, codePage);
        Write(ms, CmdLineFeed);
        Write(ms, CmdLineFeed);
        Write(ms, CmdCut);
        return ms.ToArray();
    }

    public async Task<bool> PrintPreReceiptAsync(IEnumerable<CartItem> items, decimal total)
    {
        try
        {
            await SendAsync(BuildPreReceipt(_codePage, items, total));
            SetStatus(PrinterStatus.Ready);
            return true;
        }
        catch (Exception ex)
        {
            var chain = AppLogging.DescribeChain(ex);
            Console.WriteLine($"Pre-receipt print error: {chain}");
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }

    public async Task<bool> PrintTicketAsync(string number, string? time = null, string? warehouseName = null)
    {
        try
        {
            await SendAsync(BuildTicket(_codePage, number, time, warehouseName));
            SetStatus(PrinterStatus.Ready);
            return true;
        }
        catch (Exception ex)
        {
            var chain = AppLogging.DescribeChain(ex);
            Console.WriteLine($"Ticket print error: {chain}");
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }

    public async Task<bool> PrintKitchenOrderAsync(SaleReceiptData sale, string queueNumber)
    {
        try
        {
            // sale with { QueueNumber = queueNumber }, а не sale.Items/.Subtotal/…
            // разложенные по позиционным аргументам BuildSaleReceipt: тот путь
            // собирал внутри себя новую SaleReceiptData и никогда не читал
            // sale.QueueNumber, так что любое значение этого поля молча
            // пропадало. With-выражение несёт запись целиком дальше одним
            // объектом, с бегунком в ней.
            await SendAsync(BuildSaleReceipt(_codePage, sale with { QueueNumber = queueNumber }));
            SetStatus(PrinterStatus.Ready);
            return true;
        }
        catch (Exception ex)
        {
            var chain = AppLogging.DescribeChain(ex);
            Console.WriteLine($"Kitchen order print error: {chain}");
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }

    private static void Write(MemoryStream ms, byte[] data) => ms.Write(data, 0, data.Length);

    /// <summary>ESC @ и следом ESC t n. Одним методом, а не двумя командами по
    /// месту: инициализацию пишет каждый билдер, а новый документ заводится
    /// копированием соседнего. Дописывай выбор таблицы руками — рано или поздно
    /// скопируется один ESC @ без него, и это ничем себя не выдаст: принтер не
    /// ругается на неназванную таблицу, он молча берёт свою умолчательную и
    /// печатает кириллицу мусором.</summary>
    private static void WriteInit(MemoryStream ms, EscPosCodePage codePage)
    {
        Write(ms, CmdInit);
        // Строго до ESC t: в китайском режиме выбор таблицы принтером не
        // рассматривается, и порядок здесь — не вкусовщина.
        Write(ms, CmdCancelKanji);
        Write(ms, CmdSelectCodeTable);
        ms.WriteByte(codePage.EscTSelector);   // единственное, что действительно рантайм
    }

    private static void WriteLine(MemoryStream ms, string text, EscPosCodePage codePage)
    {
        var bytes = codePage.Encoding.GetBytes(text + "\n");
        ms.Write(bytes, 0, bytes.Length);
    }
    /// <summary>protected virtual, а не private: иначе маршрутизацию документов по
    /// ролям нельзя проверить, не открыв сокет. Боевой код это не меняет — ветки
    /// транспорта остаются здесь же, ниже.</summary>
    protected virtual async Task SendAsync(byte[] data)
    {
        switch (_connectionType)
        {
            case PrinterConnectionType.COM:
                await SendViaCom(data);
                break;
            case PrinterConnectionType.LAN:
                await SendViaLan(data);
                break;
            case PrinterConnectionType.USB:
                await SendViaUsb(data);
                break;
            default:
                // До фикса ветка тихо ничего не делала: возврат без единого байта
                // ввода-вывода, который PrintPreReceiptAsync/PrintReceiptAsync и три их
                // соседа читали как успех и красили статус в Ready. Число вне диапазона
                // enum {USB,COM,LAN} попадает сюда из settings.json от правки руками или
                // неудачной миграции — System.Text.Json не проверяет диапазон enum при
                // чтении. Теперь бросает; catch всех пяти методов печати превращает это в
                // Error и false, как любой другой отказ транспорта.
                //
                // Ветка по-прежнему не делает ввода-вывода до throw — это единственное,
                // что от неё нужно двум тестам, которые нарочно строят принтер с
                // (PrinterConnectionType)99:
                // - CompositePrinterServiceTest.PrintingSurvivesASettingsChangeMidFlight
                //   (через приватный Fast) — гонке нужен мгновенный возврат без сети;
                //   исключение отсюда ловится try/catch внутри PrintPreReceiptAsync и до
                //   теста не доходит.
                // - SettingsViewModelTest.TestPrint_BuildsFromTheCodePageOnScreen_NotTheSavedOne
                //   проверяет LastTestPrintService, который сохраняется ДО await
                //   SendAsync, так что результат самой отправки тест не видит.
                // Верни эту ветку к тишине или подключи к реальному транспорту — и один из
                // них сломается не про то, ради чего заведён, вместо понятного сигнала
                // «смотри сюда».
                throw new NotSupportedException(
                    $"Unknown printer connection type {(int)_connectionType}. " +
                    "Check ConnectionType in settings.json.");
        }
    }

    private async Task SendViaCom(byte[] data)
    {
        using var port = new SerialPort(_connectionString, 9600);
        port.Open();
        await port.BaseStream.WriteAsync(data, 0, data.Length);
    }

    /// <summary>ConnectAsync и WriteAsync каждый получают свой собственный
    /// CancellationTokenSource(LanTimeout) — см. этот таймаут за числами.
    /// OperationCanceledException от НАШЕГО cts перегоняется в TimeoutException
    /// с адресом и фазой: голое OperationCanceledException пять методов печати
    /// тоже поймали бы (они ловят Exception без разбора типа), но в логе оно
    /// неотличимо от отмены, которую никто не запрашивал, а TimeoutException —
    /// ровно то исключение, которого от сетевого таймаута и ждут. Другие
    /// исключения (SocketException — принтер сам ответил отказом раньше, чем
    /// истёк бюджет) проходят как есть — это не таймаут, а честный отказ сети.
    ///
    /// Про чтение: ниже его нет и не было — SendViaLan не ждёт подтверждения от
    /// принтера, только отправляет. Защищать здесь нечего.
    ///
    /// Запись отдельным honest-тестом не покрыта: единственный воспроизводимый
    /// на этой машине способ подвесить её — переслать заведомо неисправному
    /// принтеру, который принял соединение и перестал вычитывать буфер, — на
    /// loopback этого Windows не работает ни при каком размере одной записи
    /// (проверено буквально до 128 МБ одним WriteAsync): loopback fast path
    /// копирует данные в приёмный буфер напрямую, минуя обычное управление
    /// потоком, независимо от SendBufferSize/ReceiveBufferSize. Реального
    /// подвисания в этих условиях добиться удалось только НЕСКОЛЬКИМИ отдельными
    /// WriteAsync подряд на одном соединении — SendViaLan шлёт данные одним
    /// вызовом, так что тот приём сюда не переносится, не подделывая то, что
    /// проверяется. Сам механизм — что CancellationToken действительно обрывает
    /// зависшую WriteAsync, а не просто перестаёт её ждать — проверен отдельным
    /// разовым скриптом на голых сокетах (см. отчёт по задаче), а не тестом в
    /// этом наборе: с реальным сетевым интерфейсом (не loopback) это же
    /// подвисание воспроизводится штатно, потому что fast path — свойство
    /// именно loopback-адаптера.</summary>
    private async Task SendViaLan(byte[] data)
    {
        var parts = _connectionString.Split(':');
        var host = parts[0];
        var port = parts.Length > 1 ? int.Parse(parts[1]) : 9100;
        using var client = new TcpClient();
        try
        {
            using var connectCts = new CancellationTokenSource(LanTimeout);
            await client.ConnectAsync(host, port, connectCts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"LAN printer at {_connectionString} did not accept a connection within {LanTimeout.TotalSeconds:0.#}s.");
        }

        using var stream = client.GetStream();
        try
        {
            using var writeCts = new CancellationTokenSource(LanTimeout);
            await stream.WriteAsync(data, writeCts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"LAN printer at {_connectionString} accepted the connection but did not accept the print within {LanTimeout.TotalSeconds:0.#}s.");
        }
    }

    private Task SendViaUsb(byte[] data)
    {
        // Проект таргетит net10.0, не net10.0-windows, поэтому платформенная
        // проверка обязательна. OperatingSystem.IsWindows(), а не
        // RuntimeInformation: CA1416 распознаёт её как platform guard
        // гарантированно.
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "USB printing goes through the Windows spooler and is unavailable on this OS.");
        }

        return SendViaSpoolerAsync(_connectionString, data);
    }

    /// <summary>Отдельным методом с атрибутом, а не лямбдой на месте: guard из
    /// SendViaUsb не протекает в тело лямбды, и CA1416 сработал бы на ней.
    ///
    /// Task.Run, а не синхронный вызов: OpenPrinter и StartDocPrinter — это RPC
    /// в spoolsv.exe, и на зависшем спулере они блокируются на секунды. Без него
    /// весь цикл проходил бы на UI-потоке — SendViaUsb никогда не уступал поток,
    /// а CompositePrinterService строит список задач энергичным Select. Касса
    /// замерзала бы ровно в момент закрытия продажи.</summary>
    [SupportedOSPlatform("windows")]
    private static Task SendViaSpoolerAsync(string queueName, byte[] data)
        => Task.Run(() => WindowsRawPrinter.Send(queueName, data));

    /// <summary>Исключение подписчика проглатывается намеренно. Вызовы стоят внутри
    /// try методов печати, а Invoke синхронный: без этого упавший обработчик
    /// поймался бы catch'ем печати, и чек, который физически вышел из принтера,
    /// отчитался бы как «Print failed» — кассир напечатал бы дубль.
    ///
    /// Обратный переход в Ready живёт в успешных ветках всех пяти методов печати:
    /// без него первый же отказ красил индикатор навсегда, потому что с Ready
    /// SetStatus не вызывался нигде.</summary>
    private void SetStatus(PrinterStatus status)
    {
        _status = status;
        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Printer status subscriber failed: {ex.Message}");
        }
    }

    public static byte[] BuildReturnReceipt(
        EscPosCodePage codePage,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> lines,
        decimal totalRefund, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        using var ms = new MemoryStream();
        WriteInit(ms, codePage);
        Write(ms, CmdAlignCenter);
        Write(ms, CmdDoubleSizeOn);
        WriteLine(ms, "RETURN / VOZVRAT", codePage);
        Write(ms, CmdDoubleSizeOff);
        WriteLine(ms, $"Doc #{documentNumber}", codePage);
        if (!string.IsNullOrWhiteSpace(saleDate)) WriteLine(ms, saleDate, codePage);
        if (!string.IsNullOrWhiteSpace(warehouseName)) WriteLine(ms, $"Whse: {warehouseName}", codePage);
        if (!string.IsNullOrWhiteSpace(sellerName)) WriteLine(ms, $"Seller: {sellerName}", codePage);
        WriteLine(ms, "----------------------------", codePage);
        Write(ms, CmdAlignLeft);
        foreach (var l in lines)
            WriteLine(ms, ReceiptText.PadLine($"{l.Name} x{QuantityFormat.Display(l.Quantity, "0.###")}", ReceiptText.Money(l.LineRefund), 32), codePage);
        WriteLine(ms, "----------------------------", codePage);
        Write(ms, CmdBoldOn);
        WriteLine(ms, ReceiptText.PadLine("REFUND:", ReceiptText.Money(totalRefund), 32), codePage);
        Write(ms, CmdBoldOff);
        Write(ms, CmdLineFeed);
        Write(ms, CmdLineFeed);
        Write(ms, CmdCut);
        return ms.ToArray();
    }

    public async Task<bool> PrintReturnReceiptAsync(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> lines,
        decimal totalRefund, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        try
        {
            await SendAsync(BuildReturnReceipt(_codePage, lines, totalRefund, documentNumber, warehouseName, sellerName, saleDate));
            SetStatus(PrinterStatus.Ready);
            return true;
        }
        catch (Exception ex)
        {
            var chain = AppLogging.DescribeChain(ex);
            Console.WriteLine($"Return receipt print error: {chain}");
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }

    public async Task<bool> OpenCashDrawerAsync()
    {
        try
        {
            await SendAsync(CmdDrawerKick);
            SetStatus(PrinterStatus.Ready);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cash drawer error: {ex.Message}");
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }

    /// <summary>Same ReturnReceiptLine shape as BuildReturnReceipt, split into a
    /// RETURNED and an ISSUED section, closed by a bold total line. The label
    /// carries the direction of <paramref name="difference"/> (customer owes vs.
    /// till refunds); only its absolute value is ever printed, so the cashier
    /// never has to read out a minus sign.</summary>
    public static byte[] BuildExchangeReceipt(
        EscPosCodePage codePage,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        using var ms = new MemoryStream();
        WriteInit(ms, codePage);
        Write(ms, CmdAlignCenter);
        Write(ms, CmdDoubleSizeOn);
        WriteLine(ms, "EXCHANGE / OBMEN", codePage);
        Write(ms, CmdDoubleSizeOff);
        WriteLine(ms, $"Doc #{documentNumber}", codePage);
        if (!string.IsNullOrWhiteSpace(saleDate)) WriteLine(ms, saleDate, codePage);
        if (!string.IsNullOrWhiteSpace(warehouseName)) WriteLine(ms, $"Whse: {warehouseName}", codePage);
        if (!string.IsNullOrWhiteSpace(sellerName)) WriteLine(ms, $"Seller: {sellerName}", codePage);
        WriteLine(ms, "----------------------------", codePage);
        Write(ms, CmdAlignLeft);

        WriteLine(ms, "RETURNED:", codePage);
        foreach (var l in returned)
            WriteLine(ms, ReceiptText.PadLine($"{l.Name} x{QuantityFormat.Display(l.Quantity, "0.###")}", ReceiptText.Money(l.LineRefund), 32), codePage);

        WriteLine(ms, "ISSUED:", codePage);
        foreach (var l in issued)
            WriteLine(ms, ReceiptText.PadLine($"{l.Name} x{QuantityFormat.Display(l.Quantity, "0.###")}", ReceiptText.Money(l.LineRefund), 32), codePage);

        WriteLine(ms, "----------------------------", codePage);
        Write(ms, CmdBoldOn);
        // An even swap owes nothing in either direction; without its own label it
        // printed "REFUND: 0.00" and invited the customer to ask for the money.
        var label = difference > 0 ? "AMOUNT DUE:" : difference < 0 ? "REFUND:" : "NO DIFFERENCE:";
        WriteLine(ms, ReceiptText.PadLine(label, ReceiptText.Money(Math.Abs(difference)), 32), codePage);
        Write(ms, CmdBoldOff);
        Write(ms, CmdLineFeed);
        Write(ms, CmdLineFeed);
        Write(ms, CmdCut);
        return ms.ToArray();
    }

    public async Task<bool> PrintExchangeReceiptAsync(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        try
        {
            await SendAsync(BuildExchangeReceipt(_codePage, returned, issued, difference, documentNumber, warehouseName, sellerName, saleDate));
            SetStatus(PrinterStatus.Ready);
            return true;
        }
        catch (Exception ex)
        {
            var chain = AppLogging.DescribeChain(ex);
            Console.WriteLine($"Exchange receipt print error: {chain}");
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }
}
