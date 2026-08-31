using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

/// <summary>Сервер очереди по HTTP: секрет, приём заказов и переходы состояний.
/// xunit создаёт новый экземпляр класса на каждый [Fact], так что
/// InitializeAsync/DisposeAsync отрабатывают вокруг каждого теста отдельно —
/// свой сервер на свежем порте 0 и гарантированная остановка даже если сам тест
/// упал на Assert. Два теста этого (и любого другого) класса поэтому не могут
/// столкнуться портами, сколько бы классов xunit ни распараллелил.</summary>
public class QueueServerTest : IAsyncLifetime
{
    private QueueServer _server = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // Порт 0 — операционная система выдаёт свободный. Фиксированный 8770 в
        // тестах ловил бы чужой запущенный сервер и падал через раз.
        _server = new QueueServer(new QueueStorage(TempDb()), port: 0, secret: "secret");
        var port = await _server.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        _client.DefaultRequestHeaders.Add("X-Queue-Secret", "secret");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.StopAsync();
    }

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");

    /// <summary>То же, чем на самом деле читает провод настоящий клиент
    /// (HttpQueueTransport.JsonOptions, QueueServer.WireJsonOptions) — Web
    /// defaults плюс перечисления именами. ReadFromJsonAsync без явных
    /// options по умолчанию тоже берёт Web defaults (так уже совпадали имена
    /// полей camelCase/PascalCase до этой правки), но конвертер строкового
    /// enum сам по себе не появляется — после того как сервер начал отдавать
    /// state именем, а не числом, читать его без этого поля здесь стало
    /// нечем.</summary>
    private static readonly JsonSerializerOptions ClientJsonOptions = BuildClientJsonOptions();

    private static JsonSerializerOptions BuildClientJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>Порт, который прямо сейчас никто не слушает — получаем его тем
    /// же способом, каким его выдал бы порт 0 в QueueServer, чтобы тест ниже
    /// мог назвать порт заранее (StartAsync с пустым секретом его не откроет
    /// и не вернёт) и потом честно попытаться к нему подключиться.</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static QueueOrder Order(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Number = 305,
        TillIndex = 0,
        State = QueueOrderState.New,
        CreatedAt = new DateTime(2026, 8, 31, 10, 0, 0),
        Lines = new List<QueueOrderLine> { new() { Name = "Coffee", Quantity = "2 pcs" } }
    };

    private Task<HttpResponseMessage> Move(Guid id, string state) =>
        _client.PostAsync($"orders/{id}/state", new StringContent(state, Encoding.UTF8));

    private async Task<List<QueueOrder>> Listing()
    {
        var response = await _client.GetAsync("orders");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<QueueOrder>>(ClientJsonOptions))!;
    }

    // ---- Task 13: hosting ----

    [Fact]
    public async Task AFreshServerHasNoOrders()
    {
        Assert.Empty(await Listing());
    }

    [Fact]
    public async Task ARequestWithoutTheSecretIsRejected()
    {
        using var bareClient = new HttpClient { BaseAddress = _client.BaseAddress };

        var response = await bareClient.GetAsync("orders");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Страницы кухни и табло не могут выставить заголовок — обычная
    /// загрузка со <script src> или fetch без ручной настройки его не понесёт —
    /// поэтому секрет обязан приниматься и из query-параметра.</summary>
    [Fact]
    public async Task TheSecretAlsoWorksAsAQueryParameter()
    {
        using var queryClient = new HttpClient { BaseAddress = _client.BaseAddress };

        var response = await queryClient.GetAsync("orders?secret=secret");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task AWrongSecretIsAlsoRejected()
    {
        using var wrongClient = new HttpClient { BaseAddress = _client.BaseAddress };
        wrongClient.DefaultRequestHeaders.Add("X-Queue-Secret", "not-the-secret");

        var response = await wrongClient.GetAsync("orders");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Найдено живой проверкой /board без секрета в адресе и без
    /// localStorage: голый 401 без тела оставляет вкладку браузера чисто
    /// белой — «пустой экран с ошибкой в консоли», от которого эта задача
    /// прямо предостерегает. Тело не влияет на API-клиентов (HttpQueueTransport
    /// и QueueClient судят по коду ответа, тело 401 не читают), а страницы
    /// получают хоть что-то вместо пустоты — не полноценный экран (секретную
    /// страницу без секрета всё равно не показать, это и есть весь смысл
    /// проверки), но не молчание.</summary>
    [Fact]
    public async Task ARequestWithoutTheSecretCarriesAHumanReadableBodyNotAnEmptyOne()
    {
        using var bareClient = new HttpClient { BaseAddress = _client.BaseAddress };

        var response = await bareClient.GetAsync("board");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(body));
    }

    /// <summary>Занятый порт — это неверная настройка точки, а не повод ронять
    /// кассу. Второй сервер сажаем на порт первого, который уже слушает его в
    /// рамках этого же теста — гарантированная коллизия без магических чисел.</summary>
    [Fact]
    public async Task AnOccupiedPortFailsWithoutThrowingAndExplainsWhy()
    {
        var occupiedPort = _client.BaseAddress!.Port;
        var blocked = new QueueServer(new QueueStorage(TempDb()), port: occupiedPort, secret: "secret");

        var result = await blocked.StartAsync();

        Assert.Equal(-1, result);
        Assert.False(string.IsNullOrEmpty(blocked.LastError));
    }

    /// <summary>QueueSecret пуст по умолчанию (см. SettingsData.QueueSecret) —
    /// это то состояние, в котором окажется точка, если включить роль Server и
    /// не заполнить секрет. Сервер обязан отказаться поднимать порт вовсе, а
    /// не открыть его без проверки: иначе любой телефон в гостевом Wi-Fi читал
    /// бы и правил заказы без единого пароля. -1 и LastError сами по себе не
    /// доказывают, что порт не открылся, — поэтому ниже реальная попытка
    /// подключиться, а не только проверка возвращаемого значения. Отказ TCP на
    /// закрытый порт на loopback на этой машине не мгновенный (секунды, не
    /// миллисекунды), поэтому просто ждём исключение, а не гоним его наперегонки
    /// с таймером.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptySecretRefusesToStartAndNeverOpensThePort(string emptySecret)
    {
        var port = FreePort();
        var server = new QueueServer(new QueueStorage(TempDb()), port: port, secret: emptySecret);

        var result = await server.StartAsync();

        Assert.Equal(-1, result);
        Assert.False(string.IsNullOrEmpty(server.LastError));

        using var probe = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, port));
    }

    // ---- Task 14: intake & idempotency ----

    [Fact]
    public async Task APostedOrderShowsUpInTheListingAsNew()
    {
        var order = Order();

        var post = await _client.PostAsJsonAsync("orders", order);

        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        var stored = Assert.Single(await Listing(), o => o.Id == order.Id);
        Assert.Equal(order.Number, stored.Number);
        Assert.Equal(QueueOrderState.New, stored.State);
    }

    [Fact]
    public async Task PostingTheSameOrderTwiceLeavesExactlyOne()
    {
        var order = Order();

        await _client.PostAsJsonAsync("orders", order);
        await _client.PostAsJsonAsync("orders", order);

        Assert.Single(await Listing(), o => o.Id == order.Id);
    }

    // ---- Task 15: state transitions ----

    [Fact]
    public async Task TheKitchenMovesAnOrderForward()
    {
        var order = Order();
        await _client.PostAsJsonAsync("orders", order);

        var response = await Move(order.Id, "InProgress");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<QueueOrder>(ClientJsonOptions);
        Assert.Equal(QueueOrderState.InProgress, updated!.State);
    }

    /// <summary>409, и заказ остаётся ровно там, где был — проверяем состояние
    /// после отказа, а не только код ответа: код мог бы быть правильным, а
    /// UPDATE рядом с ним — выполниться по ошибке.</summary>
    [Fact]
    public async Task AForbiddenTransitionIsRefusedAndChangesNothing()
    {
        var order = Order();
        await _client.PostAsJsonAsync("orders", order);

        // New -> Ready в обход InProgress.
        var response = await Move(order.Id, "Ready");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = Assert.Single(await Listing(), o => o.Id == order.Id);
        Assert.Equal(QueueOrderState.New, stored.State);
        Assert.Null(stored.ReadyAt);
    }

    [Fact]
    public async Task AnUnknownOrderIsNotFound()
    {
        var response = await Move(Guid.NewGuid(), "InProgress");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReadyAtAndClosedAtAreStampedWalkingTheWholeChain()
    {
        var order = Order();
        await _client.PostAsJsonAsync("orders", order);

        await Move(order.Id, "InProgress");
        var readyResponse = await Move(order.Id, "Ready");
        var closedResponse = await Move(order.Id, "Closed");

        var ready = await readyResponse.Content.ReadFromJsonAsync<QueueOrder>(ClientJsonOptions);
        var closed = await closedResponse.Content.ReadFromJsonAsync<QueueOrder>(ClientJsonOptions);

        Assert.NotNull(ready!.ReadyAt);
        Assert.Null(ready.ClosedAt);
        Assert.NotNull(closed!.ReadyAt);
        Assert.NotNull(closed.ClosedAt);
    }

    /// <summary>Часы сервера, не кассы: если бы штамп ставился из тела запроса,
    /// планшет кухни с неверными часами мог бы записать в ReadyAt что угодно.
    /// Здесь сервер сконструирован с фиксированным now, отличным от реального
    /// времени, — штамп обязан прийти именно из него.</summary>
    [Fact]
    public async Task TimestampsComeFromTheServersClockNotTheCaller()
    {
        var fixedNow = new DateTime(2030, 1, 1, 12, 0, 0);
        var server = new QueueServer(new QueueStorage(TempDb()), port: 0, secret: "secret", () => fixedNow);
        var port = await server.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        client.DefaultRequestHeaders.Add("X-Queue-Secret", "secret");
        try
        {
            var order = Order();
            await client.PostAsJsonAsync("orders", order);
            await client.PostAsync($"orders/{order.Id}/state", new StringContent("InProgress", Encoding.UTF8));

            var response = await client.PostAsync($"orders/{order.Id}/state", new StringContent("Ready", Encoding.UTF8));
            var updated = await response.Content.ReadFromJsonAsync<QueueOrder>(ClientJsonOptions);

            Assert.Equal(fixedNow, updated!.ReadyAt);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    // ---- Tasks 18-21 follow-up: state travels as a name, not a number ----
    //
    // Обнаружено при живой проверке табло и кухни на настоящем сервере
    // (см. коммит с исправлением): страницы сравнивают order.state со
    // строками ("New", "Ready"...), а QueueOrderState — обычный enum без
    // JsonConverter, так что "голый" System.Text.Json отдавал его числом
    // (0, 1, 2...). Ни один из тестов выше это не ловил — Listing() и
    // ReadFromJsonAsync<QueueOrder>() десериализуют число обратно в тот же
    // enum ничуть не хуже, чем строку, и разница исчезает раньше, чем до неё
    // доходит Assert. Тесты ниже читают тело ответа как сырой текст и
    // смотрят на него ДО разбора в QueueOrder — только так и видно, что
    // реально едет по проводу.

    /// <summary>GET /orders — сырой текст ответа, не десериализованный список.
    /// Кухонный экран и табло делают ровно то же самое: JSON.parse без
    /// промежуточного C#-типа, который бы тихо принял число вместо имени.</summary>
    [Fact]
    public async Task ThePostedOrdersStateTravelsOnTheWireAsAName()
    {
        var order = Order();
        await _client.PostAsJsonAsync("orders", order);

        var raw = await (await _client.GetAsync("orders")).Content.ReadAsStringAsync();

        Assert.Contains("\"state\":\"New\"", raw);
        Assert.DoesNotContain("\"state\":0", raw);
    }

    /// <summary>Тот же снаряд, только по ответу POST /orders/{id}/state — этот
    /// эндпоинт первым и принимает состояние именем в теле запроса ("Ready"),
    /// так что отдавать то же самое поле числом в ответе было бы особенно
    /// показательной непоследовательностью.</summary>
    [Fact]
    public async Task TheStateTransitionResponseCarriesTheStateAsAName()
    {
        var order = Order();
        await _client.PostAsJsonAsync("orders", order);

        var response = await Move(order.Id, "InProgress");
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"state\":\"InProgress\"", raw);
        Assert.DoesNotContain("\"state\":1", raw);
    }
}
