using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

/// <summary>Раздача /kds, /board и /theme.css из манифеста сборки. Секрет
/// проверяется здесь тоже, но только тем же способом, что и у остальных
/// эндпоинтов (query-параметр) — что стиль сам подхватывает секрет из адреса
/// страницы, проверяют Task 20/21 руками: страница задаёт ссылку на
/// theme.css сама, а тест на сервере этого не увидит, если случайно проверит
/// только серверную часть цепочки.</summary>
public class QueueServerStaticTest : IAsyncLifetime
{
    private QueueServer _server = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var db = Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");
        _server = new QueueServer(new QueueStorage(db), port: 0, secret: "secret");
        var port = await _server.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.StopAsync();
    }

    [Theory]
    [InlineData("kds")]
    [InlineData("board")]
    public async Task ScreensAreServedFromTheAssembly(string page)
    {
        // Секрет параметром запроса: заголовок браузеру поставить неоткуда.
        var response = await _client.GetAsync($"{page}?secret=secret");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<html", html);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TheStylesheetIsServed()
    {
        var response = await _client.GetAsync("theme.css?secret=secret");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("--primary", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AScreenWithoutTheSecretIsRefused()
    {
        var response = await _client.GetAsync("board");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Тот же отказ и у стиля, и у кухни — секрет проверяется одним
    /// middleware раньше любого маршрута (см. QueueServer.HasValidSecret), и
    /// эндпоинты статики не должны оказаться исключением из этого правила.</summary>
    [Fact]
    public async Task TheStylesheetWithoutTheSecretIsRefused()
    {
        var response = await _client.GetAsync("theme.css");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Все три заведённых маршрута (/kds, /board, /theme.css)
    /// указывают на существующие ресурсы — «ресурса нет» через HTTP снаружи
    /// не воспроизвести, такого запроса в продукте не бывает. Поэтому здесь
    /// зовём QueueServer.AssetAsync напрямую с несуществующим именем файла —
    /// он internal и виден тесту через InternalsVisibleTo в VvCash.csproj —
    /// и смотрим, что результат правда отвечает 404, исполнив IResult на
    /// настоящем HttpContext, а не полагаясь на имя типа.</summary>
    [Fact]
    public async Task AMissingAssetIsNotFoundRatherThanAnException()
    {
        var result = await QueueServer.AssetAsync("does-not-exist.html", "text/html");

        // Results.NotFound() достаёт логгер через DI при исполнении — без
        // RequestServices здесь ArgumentNullException, а не 404, и это было
        // бы не про сервер, а про недособранный тестовый HttpContext.
        var services = new ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() }
        };
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }
}
