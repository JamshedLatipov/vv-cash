using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

/// <summary>HTTP-транспорт кассы-клиента (Task 17) — против настоящего
/// QueueServer на порте 0. Настоящий сервер, а не заглушка: у loopback-петли
/// на этой машине отказ на закрытый порт ("сервер не слушает вовсе") занимает
/// ~2.2 с и превращает такие тесты в минутные, но когда порт слушает — round
/// trip быстрый, поэтому все сценарии ниже, кроме явного отказа сети, гоняются
/// против реально поднятого сервера. Свой сервер и порт в каждом тесте — тот
/// же приём, что и в QueueServerTest: тесты не сталкиваются портами.</summary>
public class HttpQueueTransportTest : IAsyncLifetime
{
    private QueueServer _server = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _server = new QueueServer(new QueueStorage(TempDb()), port: 0, secret: "secret");
        _port = await _server.StartAsync();
        Assert.True(_port > 0, $"Сервер не поднялся: {_server.LastError}");
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
    }

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");

    private static QueueOrder Order(int number, int tillIndex = 0) => new()
    {
        Id = Guid.NewGuid(),
        Number = number,
        TillIndex = tillIndex,
        State = QueueOrderState.New,
        CreatedAt = new DateTime(2026, 8, 31, 10, 0, 0),
        Lines = new List<QueueOrderLine> { new() { Name = "Coffee", Quantity = "2 pcs" } }
    };

    private HttpQueueTransport Transport(string secret = "secret") =>
        new(new HttpClient(), () => $"127.0.0.1:{_port}", () => secret);

    /// <summary>HttpMessageHandler, который считает обращения к сети и падает,
    /// если до него вообще дошло дело — ровно то, что нужно, чтобы доказать
    /// «сеть не тронута», а не просто «PostOrderAsync вернул Unreachable»:
    /// вернуть Unreachable транспорт мог бы и после неудачной попытки
    /// достучаться, а не только вместо неё.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new InvalidOperationException("Транспорт не должен был трогать сеть с пустым адресом.");
        }
    }

    /// <summary>Ловит тело исходящего запроса как есть, без похода в
    /// настоящий сервер — то, что нужно, чтобы проверить именно то, что
    /// HttpQueueTransport кладёт на провод, а не то, что настоящий
    /// QueueServer потом сумеет разобрать несмотря на расхождение.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    /// <summary>Обнаружено при живой проверке табло и кухни: QueueOrderState
    /// без явного конвертера уезжает по проводу числом (0, 1, 2...), а обе
    /// страницы сравнивают order.state со строками ("New"...). Здесь —
    /// граница транспорта: сырое тело запроса, которое реально уйдёт в сеть,
    /// а не заказ, прогнанный туда-обратно через тот же самый сериализатор
    /// (round trip через один и тот же баг ничего не поймает).</summary>
    [Fact]
    public async Task PostOrderAsyncSendsTheStateAsANameNotANumber()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var transport = new HttpQueueTransport(http, () => "127.0.0.1:1", () => "secret");

        await transport.PostOrderAsync(Order(520));

        Assert.NotNull(handler.RequestBody);
        Assert.Contains("\"state\":\"New\"", handler.RequestBody);
        Assert.DoesNotContain("\"state\":0", handler.RequestBody);
    }

    [Fact]
    public async Task ReachingARunningServerSendsTheOrder()
    {
        var transport = Transport();

        var result = await transport.PostOrderAsync(Order(501));

        Assert.Equal(PostOrderResult.Sent, result);
    }

    /// <summary>Самое важное решение задачи: неверный секрет — это ошибка
    /// настройки этой кассы, а не суждение о заказе. 401/403 обязаны стать
    /// Unreachable, а не Refused — иначе опечатка в секрете молча выбросила
    /// бы из буфера всю смену продаж (см. докстринг MapPostResult).</summary>
    [Fact]
    public async Task AWrongSecretIsUnreachableNotRefused()
    {
        var transport = Transport(secret: "wrong-secret");

        var result = await transport.PostOrderAsync(Order(502));

        Assert.Equal(PostOrderResult.Unreachable, result);
        Assert.NotEqual(PostOrderResult.Refused, result);
    }

    [Fact]
    public async Task AnEmptyAddressIsUnreachableWithoutTouchingTheNetwork()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var transport = new HttpQueueTransport(http, () => string.Empty, () => "secret");

        var result = await transport.PostOrderAsync(Order(503));

        Assert.Equal(PostOrderResult.Unreachable, result);
        Assert.False(handler.WasCalled);
    }

    /// <summary>Настоящий 4xx от настоящего сервера, не подделанный ответ:
    /// POST /orders сегодня отвечает 400 ровно в одном случае — тело
    /// разобралось в null (см. QueueServer: "if (order == null) return
    /// Results.BadRequest();"). null! здесь не имитирует сетевой сбой — это
    /// валидный (хоть и вырожденный) JSON-запрос "null", который сервер
    /// реально разбирает и реально отклоняет по существу этим же кодом,
    /// каким отклонил бы, скажем, заказ с некорректными данными. Именно
    /// такой ответ и обязан стать Refused.</summary>
    [Fact]
    public async Task AnOrderTheServerRefusesMapsToRefused()
    {
        var transport = Transport();

        var result = await transport.PostOrderAsync(null!);

        Assert.Equal(PostOrderResult.Refused, result);
    }

    /// <summary>Постим с двух касс, закрываем часть заказов на обеих — и
    /// GetClosedAsync(0) обязан вернуть только то, что закрыто у кассы 0.
    /// Без фильтра на сервере касса 0 увидела бы закрытые заказы кассы 1 и
    /// вернула бы в свой пул чужие номера (см. докстринг GetClosedAsync).</summary>
    [Fact]
    public async Task GetClosedAsyncReturnsOnlyThisTillsClosedOrders()
    {
        var transport = Transport();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/") };
        http.DefaultRequestHeaders.Add("X-Queue-Secret", "secret");

        var tillZeroClosed = Order(510, tillIndex: 0);
        var tillZeroOpen = Order(511, tillIndex: 0);
        var tillOneClosed = Order(512, tillIndex: 1);

        await transport.PostOrderAsync(tillZeroClosed);
        await transport.PostOrderAsync(tillZeroOpen);
        await transport.PostOrderAsync(tillOneClosed);

        await CloseAsync(http, tillZeroClosed.Id);
        await CloseAsync(http, tillOneClosed.Id);

        var closed = await transport.GetClosedAsync(0);

        var single = Assert.Single(closed);
        Assert.Equal(tillZeroClosed.Id, single.Id);
        // Круг замкнулся: сервер теперь отдаёт state именем, а не числом
        // (см. QueueServer.WireJsonOptions) — этот Equal проходит только
        // если HttpQueueTransport.JsonOptions (второй конец того же
        // провода) умеет это имя прочитать обратно в QueueOrderState.Closed,
        // а не молча оставляет поле в default(QueueOrderState) = New.
        Assert.Equal(QueueOrderState.Closed, single.State);
    }

    /// <summary>Проведёт заказ по всей цепочке состояний до Closed —
    /// GetClosedAsync фильтрует по state=Closed на сервере, поэтому просто
    /// отправить заказ не годится, он должен реально дойти до конца.</summary>
    private static async Task CloseAsync(HttpClient http, Guid orderId)
    {
        foreach (var step in new[] { "InProgress", "Ready", "Closed" })
        {
            var response = await http.PostAsync(
                $"orders/{orderId}/state", new StringContent(step, System.Text.Encoding.UTF8));
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>Недоступный сервер не должен ронять FlushAsync исключением —
    /// пустой список тут совершенно осознанно неотличим от «у кассы правда
    /// нет закрытых заказов»: обе ситуации требуют одного действия (ничего не
    /// делать сейчас), см. докстринг HttpQueueTransport.GetClosedAsync.</summary>
    [Fact]
    public async Task GetClosedAsyncReturnsEmptyRatherThanThrowingWhenUnreachable()
    {
        var transport = new HttpQueueTransport(new HttpClient(), () => string.Empty, () => "secret");

        var closed = await transport.GetClosedAsync(0);

        Assert.Empty(closed);
    }
}
