using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Queue;

/// <summary>HTTP-транспорт кассы-клиента к кассе-серверу очереди (Task 17) —
/// единственная реализация IQueueTransport. Адрес и секрет читаются функциями
/// на каждый вызов, а не захватываются один раз в конструкторе: настройки
/// правятся на работающей кассе (см. IQueueSettings), и транспорт,
/// держащийся за старый адрес, после правки тихо продолжал бы стучаться в
/// никуда до перезапуска приложения.</summary>
public class HttpQueueTransport : IQueueTransport
{
    private const string SecretHeader = "X-Queue-Secret";

    /// <summary>Локальная сеть отвечает за миллисекунды; несколько секунд —
    /// щедрый запас, а не экономия. Касса не должна замирать надолго только
    /// из-за того, что соседняя точка выключена — весь смысл fail-open
    /// решения (см. QueueClient) в том, чтобы недоступность сервера не
    /// стопорила продажу, а долгий таймаут на каждый заказ в буфере как раз
    /// и стопорил бы.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Те же настройки JSON, что сервер применяет ко всему, что
    /// пересекает провод (см. QueueServer.WireJsonOptions) — camelCase имена
    /// полей и перечисления именами, а не числами. Без явного options здесь
    /// тело POST ушло бы в PascalCase с State числом — сервер запрос всё
    /// равно разберёт (ReadFromJsonAsync у ASP.NET Core регистронезависим по
    /// умолчанию, а JsonStringEnumConverter принимает и число на входе), но
    /// GetClosedAsync ниже читает ответ сервера обратно тем же options, и
    /// если бы сервер вдруг начал отдавать state числом, а этот файл ждал бы
    /// строку — тут перестало бы что-то путаться молча, а не сразу.
    /// Два раздельных статических поля (здесь и в QueueServer) — не третий
    /// источник правды, а неизбежность: это два разных проекта одной сборки,
    /// делить между ними статическое поле буквально нечем, кроме как через
    /// значения этого поля оставаться идентичными, за чем и следит
    /// HttpQueueTransportTest.</summary>
    private static readonly JsonSerializerOptions JsonOptions = BuildJsonOptions();

    private static JsonSerializerOptions BuildJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private readonly HttpClient _http;
    private readonly Func<string> _address;
    private readonly Func<string> _secret;

    public HttpQueueTransport(HttpClient http, Func<string> address, Func<string> secret)
    {
        _http = http;
        _address = address;
        _secret = secret;
    }

    public async Task<PostOrderResult> PostOrderAsync(QueueOrder order)
    {
        var address = _address();
        if (string.IsNullOrWhiteSpace(address))
        {
            // Пустой адрес — касса ещё не настроена как клиент, а не «сервер
            // не отвечает», но с точки зрения FlushAsync это одно и то же:
            // досылка останавливается, буфер ждёт настройки. Сеть здесь
            // трогать незачем — результат заранее известен.
            return PostOrderResult.Unreachable;
        }

        using var cts = new CancellationTokenSource(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(address, "orders"));
            request.Headers.Add(SecretHeader, _secret());
            request.Content = JsonContent.Create(order, options: JsonOptions);

            using var response = await _http.SendAsync(request, cts.Token);
            return MapPostResult(response.StatusCode);
        }
        catch (Exception)
        {
            // Таймаут (OperationCanceledException от нашего же cts), отказ
            // соединения, DNS — всё то, что не является ответом сервера,
            // ложится в Unreachable, а не в Refused: сервер здесь вообще не
            // высказался, значит нет и приговора конкретному заказу.
            return PostOrderResult.Unreachable;
        }
    }

    /// <summary>Здесь — суть задачи. Соблазн переписать это как
    /// `response.IsSuccessStatusCode ? Sent : Refused` реален и опасен: тогда
    /// 401/403 (неверный секрет) и 5xx (сервер занемог) тоже стали бы Refused,
    /// а Refused — это «сервер посмотрел на заказ и отказал по существу»,
    /// то, что QueueClient.FlushAsync выводит из ротации навсегда (см.
    /// PostOrderResult и MarkOutboxRejectedAsync). Неверно набранный секрет —
    /// это ошибка настройки этой самой кассы, а не решение по конкретному
    /// заказу; классифицировать так заказ значило бы молча выбросить из
    /// буфера целую смену продаж при обычной опечатке в настройках, вместо
    /// того чтобы остановить всю досылку (Unreachable) и оставить буфер
    /// целым до починки секрета.</summary>
    private static PostOrderResult MapPostResult(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;

        if (code is 401 or 403)
        {
            return PostOrderResult.Unreachable;
        }

        if (code is 400 or 409 or 422)
        {
            // Сервер разобрал запрос и отказал по существу этого заказа —
            // тем же телом повторная отправка не пройдёт.
            return PostOrderResult.Refused;
        }

        if (code is >= 200 and < 300)
        {
            return PostOrderResult.Sent;
        }

        // 5xx и всё прочее, не подошедшее под ветки выше: сервер сейчас
        // нездоров, а не не согласен — то же обращение, что и у полностью
        // недоступной сети.
        return PostOrderResult.Unreachable;
    }

    /// <summary>Закрытые заказы этой кассы, чтобы вернуть их номера в пул
    /// (см. QueueClient.FlushAsync). Любой сбой — пустой список, а не
    /// исключение: этот вызов только подпитывает пул номерами, и не сделать
    /// этого прямо сейчас ничего не стоит — то же самое спросится на
    /// следующем FlushAsync. Пустой список неотличим от «у этой кассы
    /// правда нет закрытых заказов», и это осознанно: обе ситуации требуют
    /// одного и того же действия — ничего не делать, номер вернётся в пул,
    /// когда сервер станет доступен и правда что-то закроет.</summary>
    public async Task<IReadOnlyList<QueueOrder>> GetClosedAsync(int tillIndex)
    {
        var address = _address();
        if (string.IsNullOrWhiteSpace(address))
        {
            return Array.Empty<QueueOrder>();
        }

        using var cts = new CancellationTokenSource(RequestTimeout);
        try
        {
            var url = BuildUrl(address, $"orders?till={tillIndex}&state={QueueOrderState.Closed}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add(SecretHeader, _secret());

            using var response = await _http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<QueueOrder>();
            }

            var orders = await response.Content.ReadFromJsonAsync<List<QueueOrder>>(JsonOptions, cts.Token);
            return (IReadOnlyList<QueueOrder>?)orders ?? Array.Empty<QueueOrder>();
        }
        catch (Exception)
        {
            return Array.Empty<QueueOrder>();
        }
    }

    /// <summary>QueueServerAddress хранится как «10.0.0.5:8770» (см.
    /// IQueueSettings) — без схемы, для локальной сети её и не нужно
    /// указывать руками. Схема на всякий случай не дублируется, если её всё
    /// же вписали.</summary>
    private static string BuildUrl(string address, string pathAndQuery)
    {
        var trimmed = address.Trim().TrimEnd('/');
        var hasScheme =
            trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var baseUrl = hasScheme ? trimmed : $"http://{trimmed}";
        return $"{baseUrl}/{pathAndQuery}";
    }
}
