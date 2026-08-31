using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

/// <summary>Вебсокет-рассылка изменений очереди (Task 16). Экран, который
/// молча перестал обновляться, — это классический отказ такого табло: он
/// показывает вчерашние цифры, и никто не замечает. Сервер сам толкает
/// изменения через /ws, а не ждёт, пока страница спросит поллингом.
///
/// Свой сервер на порте 0 в каждом тесте — тот же приём, что и в
/// QueueServerTest (см. его докстринг): тесты не могут столкнуться портами.
/// Каждый тест ограничен CancellationTokenSource с таймаутом — зависший
/// ReceiveAsync должен уронить тест, а не подвесить весь прогон.</summary>
public class QueueServerSocketTest : IAsyncLifetime
{
    private QueueServer _server = null!;
    private HttpClient _client = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _server = new QueueServer(new QueueStorage(TempDb()), port: 0, secret: "secret");
        _port = await _server.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/") };
        _client.DefaultRequestHeaders.Add("X-Queue-Secret", "secret");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.StopAsync();
    }

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");

    private static QueueOrder Order(int number) => new()
    {
        Id = Guid.NewGuid(),
        Number = number,
        TillIndex = 0,
        State = QueueOrderState.New,
        CreatedAt = new DateTime(2026, 8, 31, 10, 0, 0),
        Lines = new List<QueueOrderLine> { new() { Name = "Coffee", Quantity = "2 pcs" } }
    };

    /// <summary>Секрет — из query-параметра: у ClientWebSocket, в отличие от
    /// HttpClient, нет удобного способа выставить заголовок так же просто,
    /// как это делает обычная страница кухни/табло через &lt;script src&gt;
    /// или fetch без ручной настройки, — тот же случай, ради которого в
    /// самом сервере секрет и принимается из URL (см. HasValidSecret).</summary>
    private async Task<ClientWebSocket> ConnectAsync(CancellationToken token)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws?secret=secret"), token);
        return socket;
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[32 * 1024];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static CancellationTokenSource Timeout() => new(TimeSpan.FromSeconds(5));

    /// <summary>Это и есть свойство, которое защищает от молчаливо устаревшего
    /// табло, и оно же — самое вероятное, что сломают позже: подключение без
    /// единого изменения в очереди уже должно принести текущий список, а не
    /// пустоту в ожидании следующего заказа.</summary>
    [Fact]
    public async Task AReconnectingSubscriberGetsTheCurrentStateImmediately()
    {
        using var cts = Timeout();
        var order = Order(410);
        var post = await _client.PostAsJsonAsync("orders", order);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        // Ничего больше не меняется — просто заново подключаемся и смотрим,
        // что придёт первым же сообщением, без единого нового заказа.
        using var socket = await ConnectAsync(cts.Token);
        var payload = await ReceiveTextAsync(socket, cts.Token);

        Assert.Contains("410", payload);
    }

    [Fact]
    public async Task APostedOrderIsPushedToASubscriber()
    {
        using var cts = Timeout();
        using var socket = await ConnectAsync(cts.Token);
        await ReceiveTextAsync(socket, cts.Token); // начальный снимок — пустой список

        await _client.PostAsJsonAsync("orders", Order(411));
        var payload = await ReceiveTextAsync(socket, cts.Token);

        Assert.Contains("411", payload);
    }

    /// <summary>Один подписчик мало что доказывает — рассылка могла бы
    /// случайно работать только для последнего добавленного в список.
    /// Подключаем двух и проверяем, что оба получили один и тот же пуш.</summary>
    [Fact]
    public async Task BothSubscribersReceiveTheBroadcast()
    {
        using var cts = Timeout();
        using var first = await ConnectAsync(cts.Token);
        using var second = await ConnectAsync(cts.Token);
        await ReceiveTextAsync(first, cts.Token);
        await ReceiveTextAsync(second, cts.Token);

        await _client.PostAsJsonAsync("orders", Order(412));

        var firstPayload = await ReceiveTextAsync(first, cts.Token);
        var secondPayload = await ReceiveTextAsync(second, cts.Token);

        Assert.Contains("412", firstPayload);
        Assert.Contains("412", secondPayload);
    }

    /// <summary>Смена состояния — второй эндпоинт, который обязан рассылать,
    /// и здесь же заодно проверяем, что рассылка после неё несёт актуальное
    /// состояние заказа, а не тот же New, с которым его завели. State уходит в
    /// JSON числом (тот же формат, что и у GET /orders — конвертера в строку
    /// нигде не настроено, см. QueueServerTest), поэтому сравниваем через
    /// десериализацию, а не поиском строки "InProgress" в сыром тексте.</summary>
    [Fact]
    public async Task AStateChangeIsAlsoPushedToSubscribers()
    {
        using var cts = Timeout();
        var order = Order(414);
        await _client.PostAsJsonAsync("orders", order);

        using var socket = await ConnectAsync(cts.Token);
        await ReceiveTextAsync(socket, cts.Token); // начальный снимок

        var move = await _client.PostAsync($"orders/{order.Id}/state",
            new StringContent("InProgress", Encoding.UTF8));
        Assert.Equal(HttpStatusCode.OK, move.StatusCode);

        var payload = await ReceiveTextAsync(socket, cts.Token);
        var pushed = JsonSerializer.Deserialize<List<QueueOrder>>(payload)!;

        var pushedOrder = Assert.Single(pushed, o => o.Id == order.Id);
        Assert.Equal(QueueOrderState.InProgress, pushedOrder.State);
    }

    /// <summary>Вкладка браузера закрывается без протокольного прощания —
    /// Abort() рвёт соединение так же резко, как обычный крестик в углу
    /// экрана. Рассылка обязана пережить это: соседний подписчик всё равно
    /// получает пуш, а сам сервер не должен упасть или отказать в приёме
    /// заказа из-за одного мёртвого сокета в списке.</summary>
    [Fact]
    public async Task ADeadSubscriberDoesNotStopTheBroadcastToOthers()
    {
        using var cts = Timeout();
        var dying = await ConnectAsync(cts.Token);
        using var survivor = await ConnectAsync(cts.Token);
        await ReceiveTextAsync(dying, cts.Token);
        await ReceiveTextAsync(survivor, cts.Token);

        dying.Abort();

        var post = await _client.PostAsJsonAsync("orders", Order(413));
        var payload = await ReceiveTextAsync(survivor, cts.Token);

        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        Assert.Contains("413", payload);

        dying.Dispose();
    }
}
