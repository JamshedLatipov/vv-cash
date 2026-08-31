using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using VvCash.Models;

namespace VvCash.Services.Queue;

/// <summary>Локальный HTTP-сервер очереди. Держит его касса, которую на смене
/// назначили сервером (QueueRole.Server) — остальные кассы, кухонный экран и
/// табло зала ходят к ней как клиенты. Интерфейс хранилища, а не конкретный
/// QueueStorage — тот же приём, что и в QueueClient (см. его докстринг):
/// эндпоинтам нужен только CRUD по заказам, а подставить в тест хранилище,
/// которое умеет отказывать по команде, можно только через интерфейс.
///
/// Провал запуска — это неверная настройка точки (порт занят, нет прав, не
/// заполнен секрет), а не повод останавливать продажи: сервер очереди —
/// надстройка над кассой, а не её часть. StartAsync поэтому ловит исключение
/// сама и сама же отказывается поднимать порт без секрета, а не бросает
/// исключение вызывающему.</summary>
public class QueueServer
{
    private const string SecretHeader = "X-Queue-Secret";
    private const string SecretQueryParam = "secret";

    private readonly IQueueStorage _storage;
    private readonly int _port;
    private readonly string _secret;
    private readonly Func<DateTime> _now;
    private WebApplication? _app;

    /// <summary>Причина последнего неудачного StartAsync. Null, пока не было ни
    /// одной попытки или последняя была успешной.</summary>
    public string? LastError { get; private set; }

    public QueueServer(IQueueStorage storage, int port, string secret, Func<DateTime>? now = null)
    {
        _storage = storage;
        _port = port;
        _secret = secret;
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>Поднимает Kestrel и возвращает реально занятый порт, либо -1,
    /// если поднять не удалось — тогда причина лежит в LastError. Порт 0
    /// (см. тесты) просит систему выдать свободный; реальный порт после этого
    /// узнаём из app.Urls, а не из того, что передали в конструктор.</summary>
    public async Task<int> StartAsync()
    {
        if (string.IsNullOrWhiteSpace(_secret))
        {
            // Пустой QueueSecret — это значение IQueueSettings.QueueSecret по
            // умолчанию, то есть именно то состояние, в котором точка окажется,
            // если включить роль Server и не заполнить секрет. HasValidSecret
            // сравнивает провайденное значение с _secret напрямую, и при пустом
            // _secret запрос вовсе без заголовка и без query-параметра тоже
            // сравнится с пустой строкой и пройдёт — сервер был бы открыт
            // любому телефону в гостевом Wi-Fi. Поэтому порт вообще не
            // поднимается: отказ виден в LastError и решается на экране
            // настроек, а не тихой дырой в проде.
            LastError = "Секрет очереди не задан (QueueSecret пуст) — сервер отказывается открывать порт без него.";
            return -1;
        }

        try
        {
            var builder = WebApplication.CreateBuilder();
            // Консольный логгер ASP.NET Core говорит поверх остального
            // консольного вывода кассы; сама касса не читает эти логи.
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(_port, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
            });

            var app = builder.Build();

            // Секрет — раньше любого эндпоинта: следующая строка после этой не
            // должна суметь тронуть storage без него. Middleware исполняются в
            // порядке регистрации, поэтому app.Use здесь стоит раньше Map*.
            app.Use(async (context, next) =>
            {
                if (!HasValidSecret(context))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                await next();
            });

            MapEndpoints(app);

            await app.StartAsync();
            _app = app;
            LastError = null;

            var boundUrl = app.Urls.FirstOrDefault();
            return boundUrl == null ? -1 : new Uri(boundUrl).Port;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return -1;
        }
    }

    /// <summary>Безопасно звать и тогда, когда StartAsync не вызывался или
    /// провалился — тест, который валится посреди сценария, не должен уронить
    /// ещё и свой собственный teardown.</summary>
    public async Task StopAsync()
    {
        if (_app == null) return;

        var app = _app;
        _app = null;
        await app.StopAsync();
        await app.DisposeAsync();
    }

    /// <summary>Секрет приезжает в заголовке — обычный клиент так и делает —
    /// либо в query-параметре: страницам кухни и табло, отданным самим этим
    /// сервером через обычный &lt;script src&gt;, заголовок выставить нечем, а
    /// URL — можно. Сравнение прямое, без отдельной проверки на пустоту: этого
    /// достаточно, потому что _secret здесь не бывает пустым или пробельным —
    /// StartAsync уже отказался поднимать порт в этом случае. Без той проверки
    /// пустой _secret значил бы, что запрос вовсе без заголовка и без
    /// query-параметра тоже сравнится с пустой строкой и пройдёт — сервер
    /// оказался бы открыт любому телефону в гостевом Wi-Fi.</summary>
    private bool HasValidSecret(HttpContext context)
    {
        var provided = context.Request.Headers[SecretHeader].ToString();
        if (string.IsNullOrEmpty(provided))
        {
            provided = context.Request.Query[SecretQueryParam].ToString();
        }
        return provided == _secret;
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/orders", async () => Results.Ok(await _storage.GetOrdersAsync()));

        app.MapPost("/orders", async (HttpContext context) =>
        {
            var order = await context.Request.ReadFromJsonAsync<QueueOrder>();
            if (order == null) return Results.BadRequest();

            await _storage.SaveOrderAsync(order);
            return Results.StatusCode(StatusCodes.Status202Accepted);
        });

        // Тело — просто имя целевого состояния ("Ready"), а не JSON-объект:
        // кухонный экран шлёт его как plain text fetch-запросом, обвязка была
        // бы накладными расходами без всякой пользы для единственного поля.
        app.MapPost("/orders/{id:guid}/state", async (Guid id, HttpContext context) =>
        {
            string body;
            using (var reader = new StreamReader(context.Request.Body))
            {
                body = (await reader.ReadToEndAsync()).Trim();
            }

            if (!Enum.TryParse<QueueOrderState>(body, ignoreCase: true, out var target))
            {
                return Results.BadRequest($"Неизвестное состояние: '{body}'.");
            }

            var order = await _storage.GetOrderAsync(id);
            if (order == null) return Results.NotFound();

            if (!QueueOrderStates.CanMove(order.State, target))
            {
                // 409, а не 400: запрос корректен по форме, конфликт — с
                // текущим состоянием заказа, которое клиент, возможно, ещё
                // не видел (задержка сети между двумя кухонными экранами).
                return Results.Conflict();
            }

            order.State = target;

            // Часы сервера, не то, что мог бы прислать клиент: у кухонного
            // планшета с неверными часами не должно быть способа записать в
            // ReadyAt/ClosedAt чепуху.
            var now = _now();
            if (target == QueueOrderState.Ready)
            {
                order.ReadyAt = now;
            }
            if (target is QueueOrderState.Closed or QueueOrderState.Cancelled)
            {
                order.ClosedAt = now;
            }

            await _storage.UpdateOrderStateAsync(order);
            return Results.Ok(order);
        });
    }
}
