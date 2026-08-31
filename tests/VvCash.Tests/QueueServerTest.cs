using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
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

    private async Task<List<QueueOrder>> Listing()
    {
        var response = await _client.GetAsync("orders");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<QueueOrder>>())!;
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
}
