using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
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

    /// <summary>Живые подписчики /ws — кухонный экран и табло зала, которым
    /// сервер сам досылает изменения. Обычный List под явным lock'ом:
    /// подписчиков разом единицы (пара экранов на точке), Kestrel обслуживает
    /// запросы параллельно, значит подключение и рассылка могут столкнуться —
    /// отсюда и lock. Снимок для рассылки берётся под ним (ToArray), а сама
    /// отправка идёт уже вне lock'а: await внутри lock не компилируется, а
    /// держать список заблокированным на время сетевого IO незачем.</summary>
    private readonly List<WebSocket> _subscribers = new();
    private readonly object _subscribersLock = new();

    /// <summary>Те же настройки JSON, что ASP.NET Core минимал-API применяет к
    /// Results.Ok по умолчанию (JsonSerializerDefaults.Web — camelCase имена
    /// полей). GET /orders их получает бесплатно через сам Results.Ok, а
    /// вебсокет собирает тело руками — без этого поля здесь оказался бы
    /// голый JsonSerializer.Serialize с именами полей в PascalCase, и
    /// кухонный экран получал бы от HTTP и от WS два разных представления
    /// одного и того же заказа: "id" в одном канале и "Id" в другом.</summary>
    private static readonly JsonSerializerOptions BroadcastJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Тайм-аут на отправку одному подписчику при рассылке. Экран
    /// может быть жив (State всё ещё Open), но зависнуть и не вычитывать
    /// сокет — без этого одна замёрзшая вкладка держала бы BroadcastOrdersAsync,
    /// а с ним и ответ на POST /orders, сколько угодно, потому что вызывающие
    /// эндпоинты ждут рассылку перед тем, как ответить кассе.</summary>
    private static readonly TimeSpan BroadcastSendTimeout = TimeSpan.FromSeconds(3);

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

            // Раньше MapEndpoints: /ws принимает апгрейд соединения, и этому
            // будущему эндпоинту нужна поддержка протокола до маршрутизации.
            app.UseWebSockets();

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
        // till и state — необязательные фильтры (Task 17): HttpQueueTransport
        // зовёт их вместе, чтобы получить закрытые заказы именно своей кассы —
        // без фильтра касса-клиент увидела бы закрытые заказы соседних касс и
        // вернула бы в свой пул чужие номера. Значение не распознано (обрезанный
        // till, опечатка в state) — фильтр просто не применяется, а не 400:
        // это внутренний служебный эндпоинт, а не форма ввода, портить ответ
        // ради строгости незачем.
        app.MapGet("/orders", async (HttpContext context) =>
        {
            IReadOnlyList<QueueOrder> orders = await _storage.GetOrdersAsync();

            if (context.Request.Query.TryGetValue("till", out var tillRaw) &&
                int.TryParse(tillRaw, out var till))
            {
                orders = orders.Where(o => o.TillIndex == till).ToList();
            }

            if (context.Request.Query.TryGetValue("state", out var stateRaw) &&
                Enum.TryParse<QueueOrderState>(stateRaw, ignoreCase: true, out var state))
            {
                orders = orders.Where(o => o.State == state).ToList();
            }

            return Results.Ok(orders);
        });

        app.MapPost("/orders", async (HttpContext context) =>
        {
            var order = await context.Request.ReadFromJsonAsync<QueueOrder>();
            if (order == null) return Results.BadRequest();

            await _storage.SaveOrderAsync(order);
            // Рассылаем и на повторной постановке того же заказа (SaveOrderAsync
            // тогда ничего не меняет): лишняя рассылка того же списка безвредна,
            // а отличать «новый» от «дубль» здесь незачем — кухня и табло просто
            // получат тот же снимок ещё раз.
            await BroadcastOrdersAsync();
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
            // Только на успешном переходе: 404 и 409 выше ничего не меняли в
            // хранилище, и рассылать в этих случаях было бы враньём той же
            // формы, от которой вся эта фича защищает, — сообщением о смене,
            // которой не было.
            await BroadcastOrdersAsync();
            return Results.Ok(order);
        });

        // Кухонный экран и табло зала подключаются сюда и просто ждут — им
        // нечего сказать серверу, весь их разговор — через POST выше; сокет
        // только слушает push. Map, а не MapGet: апгрейд-запрос браузера всё
        // равно приходит методом GET, но именно универсальный Map — тот же
        // приём, что в официальных примерах ASP.NET Core на WebSockets —
        // не заворачивает результат в Results.*, а отдаёт HttpContext
        // напрямую, что и нужно для ручного AcceptWebSocketAsync.
        app.Map("/ws", HandleWebSocketAsync);
    }

    /// <summary>Держит одно вебсокет-соединение от апгрейда до закрытия. Экран,
    /// который только что переподключился, не должен ждать следующего заказа,
    /// чтобы перестать показывать вчерашние цифры — поэтому текущий список
    /// уходит сразу же, до входа в цикл ожидания.</summary>
    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync();
        lock (_subscribersLock)
        {
            _subscribers.Add(socket);
        }

        try
        {
            await SendOrdersAsync(socket, context.RequestAborted);

            // Читать с этого сокета нечего — кухня и табло говорят обратно
            // через POST, а не через сам вебсокет, — но ReceiveAsync всё
            // равно нужен: это единственный способ узнать, что браузер закрыл
            // вкладку (получим Close-фрейм) или порвал соединение резко
            // (получим исключение, которое ловится ниже).
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
            }
        }
        catch (Exception)
        {
            // Вкладка браузера закрылась без протокольного прощания (обрыв
            // сети, аварийное закрытие) — это обычный уход подписчика, а не
            // повод уронить обработку запроса; подписчик снимается ниже, в
            // finally, тем же путём, что и при штатном закрытии.
        }
        finally
        {
            lock (_subscribersLock)
            {
                _subscribers.Remove(socket);
            }
            socket.Dispose();
        }
    }

    private async Task SendOrdersAsync(WebSocket socket, CancellationToken token)
    {
        var orders = await _storage.GetOrdersAsync();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(orders, BroadcastJsonOptions);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, token);
    }

    /// <summary>Шлёт актуальный список заказов каждому живому подписчику.
    /// Один упавший или закрывшийся сокет не должен останавливать рассылку
    /// остальным — вкладка браузера закрывается без протокольного прощания, и
    /// именно поэтому каждая отправка обёрнута в свой try: неудача с одним
    /// подписчиком не мешает дойти до следующих в снимке.</summary>
    private async Task BroadcastOrdersAsync()
    {
        WebSocket[] snapshot;
        lock (_subscribersLock)
        {
            if (_subscribers.Count == 0) return;
            snapshot = _subscribers.ToArray();
        }

        var orders = await _storage.GetOrdersAsync();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(orders, BroadcastJsonOptions);

        List<WebSocket>? dead = null;
        foreach (var socket in snapshot)
        {
            try
            {
                if (socket.State != WebSocketState.Open)
                {
                    (dead ??= new List<WebSocket>()).Add(socket);
                    continue;
                }
                using var sendTimeout = new CancellationTokenSource(BroadcastSendTimeout);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, sendTimeout.Token);
            }
            catch (Exception)
            {
                // Умер посреди рассылки или просто завис, не вычитывая свой
                // сокет (см. BroadcastSendTimeout), — не наша забота, кроме
                // как перестать его больше не звать; сам сокет закрывающий
                // цикл в HandleWebSocketAsync уберёт из списка тоже, но
                // снимок уже взят, так что дублирующий Remove здесь безвреден
                // (List просто ничего не найдёт во второй раз).
                (dead ??= new List<WebSocket>()).Add(socket);
            }
        }

        if (dead != null)
        {
            lock (_subscribersLock)
            {
                foreach (var socket in dead)
                {
                    _subscribers.Remove(socket);
                }
            }
        }
    }
}
