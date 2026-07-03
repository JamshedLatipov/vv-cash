# POS Loyalty Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Касса считает скидки через серверный движок `POST /discounts/quote/` (лояльность/промо/тиры, best-deal per-line) с fallback на кэшированный плоский % офлайн и ручной скидкой кассира сверху.

**Architecture:** Новый типизированный `QuoteService` (клиент эндпоинта). `CartService` хранит снапшот quote и считает `TotalDiscount` от него; офлайн/без карты — старый плоский путь; ручная скидка всегда сверху, кламп ≤ subtotal. `PosViewModel` дебаунс-перезапрашивает quote при изменениях корзины/клиента/промокода. `warehouse_id` берётся из cash-сессии и хранится в новом `SessionContext`.

**Tech Stack:** .NET 10, Avalonia, CommunityToolkit.Mvvm, xUnit. Тесты — `pwsh ./run-tests.ps1` (билд в `build/verify-tests`, обходит файл-лок запущенного приложения).

**Команда тестов (всегда так):**
```
pwsh ./run-tests.ps1 --filter "FullyQualifiedName~<ИмяКласса>"
```
Полный прогон: `pwsh ./run-tests.ps1`

> ⚠️ В C# отсутствующий тип ломает компиляцию всего тест-проекта. Поэтому шаг «убедись что тест падает» для нового типа = **ошибка сборки** «type/member not found». Это и есть красный TDD-шаг; реализация делает сборку зелёной.

---

## Структура файлов

**Создать:**
- `src/VvCash/Models/Api/QuoteModels.cs` — DTO запроса/ответа `/discounts/quote/`
- `src/VvCash/Services/Api/IQuoteService.cs` + `QuoteService.cs` — клиент эндпоинта
- `src/VvCash/Services/ISessionContext.cs` + `SessionContext.cs` — in-memory держатель `WarehouseId`
- `src/VvCash/Services/QuoteRequestBuilder.cs` — чистый билдер `QuoteRequest` из корзины
- `src/VvCash/Services/QuoteLineResolver.cs` — чистый маппер per-line скидки для чека
- Тесты: `tests/VvCash.Tests/QuoteModelsTest.cs`, `QuoteServiceTest.cs`, `CartServiceQuoteTest.cs`, `QuoteRequestBuilderTest.cs`, `QuoteLineResolverTest.cs`

**Изменить:**
- `src/VvCash/Services/ICartService.cs` + `CartService.cs` — снапшот quote + новая математика
- `src/VvCash/Services/Api/IShiftService.cs` + `ShiftService.cs` — вытащить warehouse из cash-сессии в `SessionContext`
- `src/VvCash/ViewModels/PosViewModel.cs` — оркестрация requote, промокод→`code`, маппинг чека
- `src/VvCash/App.axaml.cs` — DI: `IQuoteService`, `ISessionContext`

---

## Task 1: Quote DTO модели

**Files:**
- Create: `src/VvCash/Models/Api/QuoteModels.cs`
- Test: `tests/VvCash.Tests/QuoteModelsTest.cs`

- [ ] **Step 1: Написать падающий тест**

```csharp
// tests/VvCash.Tests/QuoteModelsTest.cs
using System.Text.Json;
using VvCash.Models.Api;
using Xunit;

namespace VvCash.Tests;

public class QuoteModelsTest
{
    [Fact]
    public void QuoteResult_DeserializesSnakeCaseFromServer()
    {
        const string json = """
        {
          "quote_id":"q1","subtotal":100,"discount_total":15,"total":85,
          "lines":[{"product_id":"p1","quantity":2,"unit_price":50,"line_subtotal":100,
                    "discount_amount":15,"discount_percent":15,"final_line_total":85,
                    "source":{"kind":"card","ref":"c1"}}],
          "applied":[{"kind":"loyalty","amount":15,"ref":"c1"}],
          "rejected":[{"reason":"expired","ref":"PROMO5"}]
        }
        """;

        var r = JsonSerializer.Deserialize<QuoteResult>(json)!;

        Assert.Equal("q1", r.QuoteId);
        Assert.Equal(15m, r.DiscountTotal);
        Assert.Single(r.Lines);
        Assert.Equal("p1", r.Lines[0].ProductId);
        Assert.Equal(15m, r.Lines[0].DiscountPercent);
        Assert.Equal("card", r.Lines[0].Source!.Kind);
        Assert.Equal("loyalty", r.Applied[0].Kind);
        Assert.Equal("expired", r.Rejected[0].Reason);
    }

    [Fact]
    public void QuoteRequest_SerializesSnakeCaseAndOmitsNulls()
    {
        var req = new QuoteRequest
        {
            WarehouseId = "w1",
            Lines = new() { new QuoteLineInput { ProductId = "p1", Quantity = 1, UnitPrice = 10 } }
        };

        var json = JsonSerializer.Serialize(req);

        Assert.Contains("\"warehouse_id\":\"w1\"", json);
        Assert.Contains("\"product_id\":\"p1\"", json);
        Assert.DoesNotContain("card_identifier", json); // null опущен
        Assert.DoesNotContain("\"code\"", json);
    }
}
```

- [ ] **Step 2: Запустить — убедиться что падает**

Run: `pwsh ./run-tests.ps1 --filter "FullyQualifiedName~QuoteModelsTest"`
Expected: ошибка сборки — `QuoteResult`/`QuoteRequest` не найдены.

- [ ] **Step 3: Реализовать модели**

```csharp
// src/VvCash/Models/Api/QuoteModels.cs
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VvCash.Models.Api;

public class QuoteRequest
{
    [JsonPropertyName("warehouse_id")] public string WarehouseId { get; set; } = string.Empty;

    [JsonPropertyName("card_identifier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CardIdentifier { get; set; }

    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }

    [JsonPropertyName("lines")] public List<QuoteLineInput> Lines { get; set; } = new();
}

public class QuoteLineInput
{
    [JsonPropertyName("product_id")] public string ProductId { get; set; } = string.Empty;
    [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
    [JsonPropertyName("unit_price")] public decimal UnitPrice { get; set; }
}

public class QuoteResult
{
    [JsonPropertyName("quote_id")] public string QuoteId { get; set; } = string.Empty;
    [JsonPropertyName("subtotal")] public decimal Subtotal { get; set; }
    [JsonPropertyName("discount_total")] public decimal DiscountTotal { get; set; }
    [JsonPropertyName("total")] public decimal Total { get; set; }
    [JsonPropertyName("lines")] public List<QuoteLineResult> Lines { get; set; } = new();
    [JsonPropertyName("applied")] public List<QuoteApplied> Applied { get; set; } = new();
    [JsonPropertyName("rejected")] public List<QuoteRejected> Rejected { get; set; } = new();
}

public class QuoteLineResult
{
    [JsonPropertyName("product_id")] public string ProductId { get; set; } = string.Empty;
    [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
    [JsonPropertyName("unit_price")] public decimal UnitPrice { get; set; }
    [JsonPropertyName("line_subtotal")] public decimal LineSubtotal { get; set; }
    [JsonPropertyName("discount_amount")] public decimal DiscountAmount { get; set; }
    [JsonPropertyName("discount_percent")] public decimal DiscountPercent { get; set; }
    [JsonPropertyName("final_line_total")] public decimal FinalLineTotal { get; set; }
    [JsonPropertyName("source")] public QuoteSource? Source { get; set; }
}

public class QuoteSource
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("ref")] public string Ref { get; set; } = string.Empty;
}

public class QuoteApplied
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("amount")] public decimal Amount { get; set; }
    [JsonPropertyName("ref")] public string Ref { get; set; } = string.Empty;
}

public class QuoteRejected
{
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    [JsonPropertyName("ref")] public string Ref { get; set; } = string.Empty;
}
```

- [ ] **Step 4: Запустить — убедиться что зелёный**

Run: `pwsh ./run-tests.ps1 --filter "FullyQualifiedName~QuoteModelsTest"`
Expected: PASS (2 теста).

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Models/Api/QuoteModels.cs tests/VvCash.Tests/QuoteModelsTest.cs
git commit -m "feat: add quote DTOs for /discounts/quote/"
```

---

## Task 2: IQuoteService / QuoteService

Сервер по swagger возвращает `QuoteResult` напрямую (200). Но прочие proffi-эндпоинты часто заворачивают в `{status, body, message}`. Клиент терпим к обоим: если в корне есть `body` — разворачиваем.

**Files:**
- Create: `src/VvCash/Services/Api/IQuoteService.cs`, `src/VvCash/Services/Api/QuoteService.cs`
- Test: `tests/VvCash.Tests/QuoteServiceTest.cs`

- [ ] **Step 1: Написать падающий тест**

```csharp
// tests/VvCash.Tests/QuoteServiceTest.cs
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using Xunit;

namespace VvCash.Tests;

public class QuoteServiceTest
{
    private sealed class FakeSettings : ISettingsService
    {
        public string BackendUrl { get; set; } = "https://example.test/api/v1/";
        public string CashRegisterToken { get; set; } = "";
        public string AuthToken { get; set; } = "";
        public System.DateTime? AuthTokenExpiresAt { get; set; }
        public int SyncIntervalMinutes { get; set; } = 10;
        public string Language { get; set; } = "ru";
        public System.Collections.Generic.List<VvCash.Models.PrinterConfig> Printers { get; set; } = new();
        public bool ReturnOpenCashDrawer { get; set; } = true;
        public bool ReturnPrintReceipt { get; set; } = true;
        public event System.EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, System.EventArgs.Empty);
    }

    private static QuoteService Build(StubHttpMessageHandler h)
        => new QuoteService(new HttpClient(h), new FakeSettings());

    private static QuoteRequest Req() => new()
    {
        WarehouseId = "w1",
        Lines = new() { new QuoteLineInput { ProductId = "p1", Quantity = 1, UnitPrice = 10 } }
    };

    [Fact]
    public async Task QuoteAsync_PostsToEndpoint_ParsesDirectResult()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """{"quote_id":"q1","subtotal":10,"discount_total":1,"total":9,"lines":[],"applied":[],"rejected":[]}"""));
        var svc = Build(handler);

        var r = await svc.QuoteAsync(Req(), CancellationToken.None);

        Assert.NotNull(r);
        Assert.Equal("q1", r!.QuoteId);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("discounts/quote/", handler.LastRequest.RequestUri!.ToString());
        Assert.Contains("\"warehouse_id\":\"w1\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task QuoteAsync_UnwrapsEnvelope()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """{"status":0,"message":"ok","body":{"quote_id":"q2","discount_total":5,"lines":[],"applied":[],"rejected":[]}}"""));
        var svc = Build(handler);

        var r = await svc.QuoteAsync(Req(), CancellationToken.None);

        Assert.Equal("q2", r!.QuoteId);
        Assert.Equal(5m, r.DiscountTotal);
    }

    [Fact]
    public async Task QuoteAsync_ReturnsNullOnNon200()
    {
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.BadRequest, """{"message":"bad"}"""));
        var svc = Build(handler);

        Assert.Null(await svc.QuoteAsync(Req(), CancellationToken.None));
    }
}
```

- [ ] **Step 2: Запустить — убедиться что падает**

Run: `pwsh ./run-tests.ps1 --filter "FullyQualifiedName~QuoteServiceTest"`
Expected: ошибка сборки — `QuoteService` не найден.

- [ ] **Step 3: Реализовать интерфейс и сервис**

```csharp
// src/VvCash/Services/Api/IQuoteService.cs
using System.Threading;
using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public interface IQuoteService
{
    Task<QuoteResult?> QuoteAsync(QuoteRequest request, CancellationToken ct);
}
```

```csharp
// src/VvCash/Services/Api/QuoteService.cs
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public class QuoteService : IQuoteService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public QuoteService(HttpClient httpClient, ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
    }

    private string GetBaseUrl()
    {
        var baseUrl = _settingsService.BackendUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;
        if (!baseUrl.EndsWith("/")) baseUrl += "/";
        return baseUrl;
    }

    public async Task<QuoteResult?> QuoteAsync(QuoteRequest request, CancellationToken ct)
    {
        try
        {
            var baseUrl = GetBaseUrl();
            if (string.IsNullOrEmpty(baseUrl)) return null;

            var resp = await _httpClient.PostAsJsonAsync($"{baseUrl}discounts/quote/", request, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var content = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            // Терпим к обёртке {status, body, message}.
            var target = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("body", out var body)
                ? body
                : root;

            return JsonSerializer.Deserialize<QuoteResult>(target.GetRawText());
        }
        catch (Exception)
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: Запустить — убедиться что зелёный**

Run: `pwsh ./run-tests.ps1 --filter "FullyQualifiedName~QuoteServiceTest"`
Expected: PASS (3 теста).

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Api/IQuoteService.cs src/VvCash/Services/Api/QuoteService.cs tests/VvCash.Tests/QuoteServiceTest.cs
git commit -m "feat: add QuoteService client for /discounts/quote/"
```

---

## Task 3: SessionContext + warehouse_id из cash-сессии

`/cashes/shift/open/` и `/cashes/shift/state/` возвращают нетипизированное `body`.
⚠️ **Открытый пункт:** точное имя поля склада неизвестно. Реализуем чтение с попыткой
`warehouse_id`, затем `warehouse` (если объект — берём вложенный `id`). Поле подтвердить
по логу живого ответа (см. Step 6 проверки).

**Files:**
- Create: `src/VvCash/Services/ISessionContext.cs`, `src/VvCash/Services/SessionContext.cs`
- Modify: `src/VvCash/Services/Api/ShiftService.cs`
- Test: добавить кейсы в `tests/VvCash.Tests/` (новый `WarehouseExtractTest.cs`)

- [ ] **Step 1: Написать падающий тест на извлечение warehouse**

Извлечение выносим в `static` метод `ShiftService.ExtractWarehouseId(JsonElement body)` ради чистого юнита.

```csharp
// tests/VvCash.Tests/WarehouseExtractTest.cs
using System.Text.Json;
using VvCash.Services.Api;
using Xunit;

namespace VvCash.Tests;

public class WarehouseExtractTest
{
    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ExtractWarehouseId_FromFlatField()
    {
        var id = ShiftService.ExtractWarehouseId(Body("""{"id":"s1","warehouse_id":"w-123"}"""));
        Assert.Equal("w-123", id);
    }

    [Fact]
    public void ExtractWarehouseId_FromNestedObject()
    {
        var id = ShiftService.ExtractWarehouseId(Body("""{"id":"s1","warehouse":{"id":"w-456","name":"Main"}}"""));
        Assert.Equal("w-456", id);
    }

    [Fact]
    public void ExtractWarehouseId_NullWhenAbsent()
    {
        Assert.Null(ShiftService.ExtractWarehouseId(Body("""{"id":"s1"}""")));
    }
}
```

- [ ] **Step 2: Запустить — убедиться что падает**

Run: `pwsh ./run-tests.ps1 --filter "FullyQualifiedName~WarehouseExtractTest"`
Expected: ошибка сборки — `ShiftService.ExtractWarehouseId` не найден.

- [ ] **Step 3: Реализовать SessionContext**

```csharp
// src/VvCash/Services/ISessionContext.cs
namespace VvCash.Services;

/// <summary>In-memory состояние текущей кассовой сессии (не персистится).</summary>
public interface ISessionContext
{
    string? WarehouseId { get; set; }
}
```

```csharp
// src/VvCash/Services/SessionContext.cs
namespace VvCash.Services;

public class SessionContext : ISessionContext
{
    public string? WarehouseId { get; set; }
}
```

- [ ] **Step 4: Добавить `ExtractWarehouseId` и проброс в `ShiftService`**

В `src/VvCash/Services/Api/ShiftService.cs`:

Добавить `using System.Text.Json;` (уже есть) и `using VvCash.Services;` (для `ISessionContext`).

Заменить конструктор и поля:

```csharp
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly ISessionContext _session;

    public ShiftService(HttpClient httpClient, ISettingsService settingsService, ISessionContext session)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _session = session;
    }

    /// <summary>Тянет warehouse id из нетипизированного body cash-сессии.
    /// Пробует "warehouse_id", затем "warehouse" (плоское значение или вложенный объект с "id").</summary>
    public static string? ExtractWarehouseId(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;

        if (body.TryGetProperty("warehouse_id", out var wid) && wid.ValueKind == JsonValueKind.String)
            return wid.GetString();

        if (body.TryGetProperty("warehouse", out var w))
        {
            if (w.ValueKind == JsonValueKind.String) return w.GetString();
            if (w.ValueKind == JsonValueKind.Object && w.TryGetProperty("id", out var nid) && nid.ValueKind == JsonValueKind.String)
                return nid.GetString();
        }
        return null;
    }
```

В `OpenShiftAsync`, внутри блока где найден `bodyElement` с `id` (после `if (root.TryGetProperty("body", out var bodyElement) ...)`), добавить установку склада. Заменить:

```csharp
                    if (root.TryGetProperty("body", out var bodyElement) && bodyElement.TryGetProperty("id", out var idElement))
                    {
                        return idElement.GetString();
                    }
```

на:

```csharp
                    if (root.TryGetProperty("body", out var bodyElement) && bodyElement.TryGetProperty("id", out var idElement))
                    {
                        var wh = ExtractWarehouseId(bodyElement);
                        if (!string.IsNullOrEmpty(wh)) _session.WarehouseId = wh;
                        return idElement.GetString();
                    }
```

В `GetShiftStateAsync`, аналогично в блоке с `bodyElement`/`idElement`, перед `return idElement.GetString();` добавить:

```csharp
                            var wh = ExtractWarehouseId(bodyElement);
                            if (!string.IsNullOrEmpty(wh)) _session.WarehouseId = wh;
```

- [ ] **Step 5: Запустить — убедиться что зелёный**

Run: `pwsh ./run-tests.ps1 --filter "FullyQualifiedName~WarehouseExtractTest"`
Expected: PASS (3 теста).

> Полный прогон позже подтвердит, что добавленный аргумент конструктора `ShiftService` не сломал прочие тесты (его инстанцирует только DI и, возможно, тесты — на момент написания прямых тестов `ShiftService` нет).

- [ ] **Step 6: (Проверка против живого бэкенда — ручная, во время интеграции)**

Запустить кассу, открыть смену, в логах `[ShiftService] Response content:` найти поле склада.
Если имя не `warehouse_id`/`warehouse` — расширить `ExtractWarehouseId` нужным ключом и обновить тест.

- [ ] **Step 7: Коммит**

```bash
git add src/VvCash/Services/ISessionContext.cs src/VvCash/Services/SessionContext.cs src/VvCash/Services/Api/ShiftService.cs tests/VvCash.Tests/WarehouseExtractTest.cs
git commit -m "feat: capture warehouse id from cash session into SessionContext"
```

---

## Task 4: CartService — снапшот quote и новая математика скидки

**Files:**
- Modify: `src/VvCash/Services/ICartService.cs`, `src/VvCash/Services/CartService.cs`
- Test: `tests/VvCash.Tests/CartServiceQuoteTest.cs`

- [ ] **Step 1: Написать падающий тест**

```csharp
// tests/VvCash.Tests/CartServiceQuoteTest.cs
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class CartServiceQuoteTest
{
    private static CartService CartWith(decimal price, int qty)
    {
        var c = new CartService();
        var p = new Product { Id = "p1", Name = "X", Price = price };
        for (int i = 0; i < qty; i++) c.AddProduct(p);
        return c;
    }

    [Fact]
    public void TotalDiscount_UsesQuoteDiscountTotal_WhenApplied()
    {
        var c = CartWith(100m, 1); // subtotal 100
        c.ApplyQuote(new QuoteResult { QuoteId = "q1", DiscountTotal = 20m });

        Assert.Equal("q1", c.QuoteId);
        Assert.Equal(20m, c.TotalDiscount);
        Assert.Equal(80m, c.TotalAmount);
    }

    [Fact]
    public void TotalDiscount_StacksManualOnTopOfQuote()
    {
        var c = CartWith(100m, 1);
        c.ApplyQuote(new QuoteResult { QuoteId = "q1", DiscountTotal = 20m });
        c.SetManualDiscount(0m, 5m); // +5 сверху

        Assert.Equal(25m, c.TotalDiscount);
    }

    [Fact]
    public void TotalDiscount_ClampedToSubtotal()
    {
        var c = CartWith(100m, 1);
        c.ApplyQuote(new QuoteResult { QuoteId = "q1", DiscountTotal = 90m });
        c.SetManualDiscount(0m, 50m); // 90+50 > 100

        Assert.Equal(100m, c.TotalDiscount);
        Assert.Equal(0m, c.TotalAmount);
    }

    [Fact]
    public void ClearQuote_FallsBackToFlatCustomerPercent()
    {
        var c = CartWith(100m, 1);
        c.ApplyQuote(new QuoteResult { QuoteId = "q1", DiscountTotal = 20m });
        c.ClearQuote();
        c.SetCustomerDiscount(10m); // плоский 10%

        Assert.Null(c.QuoteId);
        Assert.Equal(10m, c.TotalDiscount);
    }

    [Fact]
    public void ClearCart_ClearsQuote()
    {
        var c = CartWith(100m, 1);
        c.ApplyQuote(new QuoteResult { QuoteId = "q1", DiscountTotal = 20m });
        c.ClearCart();

        Assert.Null(c.Quote);
        Assert.Null(c.QuoteId);
    }
}
```

- [ ] **Step 2: Запустить — убедиться что падает**

Run: `pwsh ./run-tests.ps1 --filter "FullyQualifiedName~CartServiceQuoteTest"`
Expected: ошибка сборки — `ApplyQuote`/`QuoteId`/`Quote`/`ClearQuote` не найдены.

- [ ] **Step 3: Расширить интерфейс**

В `src/VvCash/Services/ICartService.cs` добавить (рядом с другими членами, нужен `using VvCash.Models.Api;`):

```csharp
    // Server-quoted discount snapshot (null => offline/flat fallback)
    VvCash.Models.Api.QuoteResult? Quote { get; }
    string? QuoteId { get; }
    void ApplyQuote(VvCash.Models.Api.QuoteResult result);
    void ClearQuote();
```

- [ ] **Step 4: Реализовать в CartService**

В `src/VvCash/Services/CartService.cs` добавить `using VvCash.Models.Api;`.

Добавить поля/свойства (рядом с другими `public decimal ... { get; private set; }`):

```csharp
    public QuoteResult? Quote { get; private set; }
    public string? QuoteId => Quote?.QuoteId;
```

Заменить геттер `TotalDiscount` целиком на:

```csharp
    public decimal TotalDiscount
    {
        get
        {
            var subtotal = Subtotal;

            decimal baseDiscount;
            if (Quote != null)
            {
                // Server best-deal уже включает loyalty/promo/тиры.
                baseDiscount = Quote.DiscountTotal;
            }
            else
            {
                // Offline/без карты: старый плоский путь.
                var couponPercent = _appliedCoupons.Sum(c => c.DiscountPercent) / 100m * subtotal;
                var couponFlat = _appliedCoupons.Sum(c => c.DiscountAmount);
                var customerPercent = CustomerDiscountPercent / 100m * subtotal;
                baseDiscount = couponPercent + couponFlat + customerPercent;
            }

            // Ручная скидка кассира всегда сверху.
            var manualPercent = ManualDiscountPercent / 100m * subtotal;
            var manualFlat = ManualDiscountAmount;

            var total = baseDiscount + manualPercent + manualFlat;
            return Math.Min(total, subtotal);
        }
    }
```

Добавить методы (рядом с `SetCustomerDiscount`):

```csharp
    public void ApplyQuote(QuoteResult result)
    {
        Quote = result;
        RaiseCartChanged();
    }

    public void ClearQuote()
    {
        Quote = null;
        RaiseCartChanged();
    }
```

В `ClearCart()` добавить `ClearQuote();` (перед `RaiseCartChanged()`), и в `ClearCustomerDiscount()` — тоже `Quote = null;`. Конкретно заменить `ClearCart`:

```csharp
    public void ClearCart()
    {
        _items.Clear();
        _appliedCoupons.Clear();
        Quote = null;
        ClearManualDiscount();
        RaiseCartChanged();
    }
```

- [ ] **Step 5: Запустить — убедиться что зелёный**

Run: `pwsh ./run-tests.ps1 --filter "FullyQualifiedName~CartServiceQuoteTest"`
Expected: PASS (5 тестов).

- [ ] **Step 6: Коммит**

```bash
git add src/VvCash/Services/ICartService.cs src/VvCash/Services/CartService.cs tests/VvCash.Tests/CartServiceQuoteTest.cs
git commit -m "feat: CartService holds server quote snapshot, manual stacks on top"
```

---

## Task 5: QuoteRequestBuilder (чистый билдер запроса)

**Files:**
- Create: `src/VvCash/Services/QuoteRequestBuilder.cs`
- Test: `tests/VvCash.Tests/QuoteRequestBuilderTest.cs`

- [ ] **Step 1: Написать падающий тест**

```csharp
// tests/VvCash.Tests/QuoteRequestBuilderTest.cs
using System.Collections.Generic;
using VvCash.Models;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class QuoteRequestBuilderTest
{
    private static List<CartItem> Cart() => new()
    {
        new CartItem { Product = new Product { Id = "p1", Price = 10m }, Quantity = 2 },
        new CartItem { Product = new Product { Id = "p2", Price = 5m }, Quantity = 1 },
    };

    [Fact]
    public void Build_MapsLinesAndIdentifiers()
    {
        var req = QuoteRequestBuilder.Build(Cart(), "w1", "CARD-7", "PROMO5");

        Assert.Equal("w1", req.WarehouseId);
        Assert.Equal("CARD-7", req.CardIdentifier);
        Assert.Equal("PROMO5", req.Code);
        Assert.Equal(2, req.Lines.Count);
        Assert.Equal("p1", req.Lines[0].ProductId);
        Assert.Equal(2m, req.Lines[0].Quantity);
        Assert.Equal(10m, req.Lines[0].UnitPrice);
    }

    [Fact]
    public void Build_BlankCardAndCodeBecomeNull()
    {
        var req = QuoteRequestBuilder.Build(Cart(), "w1", "  ", "");

        Assert.Null(req.CardIdentifier);
        Assert.Null(req.Code);
    }
}
```

- [ ] **Step 2: Запустить — убедиться что падает**

Run: `pwsh ./run-tests.ps1 --filter "FullyQualifiedName~QuoteRequestBuilderTest"`
Expected: ошибка сборки — `QuoteRequestBuilder` не найден.

- [ ] **Step 3: Реализовать билдер**

```csharp
// src/VvCash/Services/QuoteRequestBuilder.cs
using System.Collections.Generic;
using System.Linq;
using VvCash.Models;
using VvCash.Models.Api;

namespace VvCash.Services;

public static class QuoteRequestBuilder
{
    public static QuoteRequest Build(IEnumerable<CartItem> items, string warehouseId, string? cardIdentifier, string? code)
    {
        return new QuoteRequest
        {
            WarehouseId = warehouseId,
            CardIdentifier = string.IsNullOrWhiteSpace(cardIdentifier) ? null : cardIdentifier.Trim(),
            Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
            Lines = items.Select(i => new QuoteLineInput
            {
                ProductId = i.Product.Id,
                Quantity = i.Quantity,
                UnitPrice = i.Product.Price
            }).ToList()
        };
    }
}
```

- [ ] **Step 4: Запустить — убедиться что зелёный**

Run: `pwsh ./run-tests.ps1 --filter "FullyQualifiedName~QuoteRequestBuilderTest"`
Expected: PASS (2 теста).

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/QuoteRequestBuilder.cs tests/VvCash.Tests/QuoteRequestBuilderTest.cs
git commit -m "feat: add QuoteRequestBuilder"
```

---

## Task 6: QuoteLineResolver (per-line скидка для чека)

**Files:**
- Create: `src/VvCash/Services/QuoteLineResolver.cs`
- Test: `tests/VvCash.Tests/QuoteLineResolverTest.cs`

- [ ] **Step 1: Написать падающий тест**

```csharp
// tests/VvCash.Tests/QuoteLineResolverTest.cs
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class QuoteLineResolverTest
{
    [Fact]
    public void Resolve_UsesQuoteLine_WhenPresent()
    {
        var quote = new QuoteResult
        {
            Lines = { new QuoteLineResult { ProductId = "p1", DiscountPercent = 15m, UnitPrice = 100m } }
        };
        var item = new CartItem { Product = new Product { Id = "p1", Price = 100m }, Quantity = 1 };

        var (pct, before) = QuoteLineResolver.Resolve(quote, item);

        Assert.Equal(15m, pct);
        Assert.Equal(100m, before);
    }

    [Fact]
    public void Resolve_FallsBackToProduct_WhenNoQuote()
    {
        var item = new CartItem
        {
            Product = new Product { Id = "p9", Price = 80m, OriginalPrice = 90m, DiscountPercent = 10m },
            Quantity = 1
        };

        var (pct, before) = QuoteLineResolver.Resolve(null, item);

        Assert.Equal(10m, pct);
        Assert.Equal(90m, before);
    }

    [Fact]
    public void Resolve_FallsBackToProduct_WhenLineMissingInQuote()
    {
        var quote = new QuoteResult { Lines = { new QuoteLineResult { ProductId = "other" } } };
        var item = new CartItem { Product = new Product { Id = "p1", Price = 50m }, Quantity = 1 };

        var (pct, before) = QuoteLineResolver.Resolve(quote, item);

        Assert.Equal(0m, pct);
        Assert.Equal(50m, before);
    }
}
```

- [ ] **Step 2: Запустить — убедиться что падает**

Run: `pwsh ./run-tests.ps1 --filter "FullyQualifiedName~QuoteLineResolverTest"`
Expected: ошибка сборки — `QuoteLineResolver` не найден.

- [ ] **Step 3: Реализовать резолвер**

```csharp
// src/VvCash/Services/QuoteLineResolver.cs
using System.Linq;
using VvCash.Models;
using VvCash.Models.Api;

namespace VvCash.Services;

public static class QuoteLineResolver
{
    /// <summary>Возвращает (discountPercent, priceBeforeDiscount) для строки чека.
    /// Серверный quote приоритетнее; иначе — плоские поля продукта.</summary>
    public static (decimal discountPercent, decimal priceBeforeDiscount) Resolve(QuoteResult? quote, CartItem item)
    {
        if (quote != null)
        {
            var line = quote.Lines.FirstOrDefault(l => l.ProductId == item.Product.Id);
            if (line != null)
                return (line.DiscountPercent, line.UnitPrice);
        }
        return (item.Product.DiscountPercent ?? 0m, item.Product.OriginalPrice ?? item.Product.Price);
    }
}
```

- [ ] **Step 4: Запустить — убедиться что зелёный**

Run: `pwsh ./run-tests.ps1 --filter "FullyQualifiedName~QuoteLineResolverTest"`
Expected: PASS (3 теста).

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/QuoteLineResolver.cs tests/VvCash.Tests/QuoteLineResolverTest.cs
git commit -m "feat: add QuoteLineResolver for receipt per-line discount"
```

---

## Task 7: DI-проводка

**Files:**
- Modify: `src/VvCash/App.axaml.cs`

- [ ] **Step 1: Зарегистрировать сервисы**

В `ConfigureServices`, в блоке `// Core Services` после `IOfflineStorageService` добавить:

```csharp
        services.AddSingleton<ISessionContext, SessionContext>();
```

В блоке `// POS Services` после `services.AddSingleton<IDiscountService, DiscountService>();` добавить:

```csharp
        services.AddHttpClient<IQuoteService, QuoteService>().AddHttpMessageHandler<AuthHeaderHandler>();
```

- [ ] **Step 2: Сборка тест-проекта (smoke)**

Run: `pwsh ./run-tests.ps1 --filter "FullyQualifiedName~SmokeTest"`
Expected: PASS — проект приложения и тесты компилируются с новыми DI-регистрациями (включая новый аргумент конструктора `ShiftService`, который резолвит DI).

- [ ] **Step 3: Коммит**

```bash
git add src/VvCash/App.axaml.cs
git commit -m "chore: register QuoteService and SessionContext in DI"
```

---

## Task 8: PosViewModel — оркестрация requote + промокод + чек

Связывает всё: дебаунс-перезапрос quote при изменениях, online-гейт, fallback,
промокод через `code`, статус по `applied`/`rejected`, маппинг чека через `QuoteLineResolver`.

**Files:**
- Modify: `src/VvCash/ViewModels/PosViewModel.cs`

> Логика разбита на чистые helper-ы (Tasks 5–6), которые уже покрыты юнит-тестами.
> Здесь — проводка во VM; верификация сборкой + ручной прогон (Task 9).

- [ ] **Step 1: Добавить зависимости в конструктор**

В список полей PosViewModel добавить:

```csharp
    private readonly IQuoteService _quoteService;
    private readonly ISessionContext _session;
    private System.Threading.CancellationTokenSource? _quoteCts;
    private bool _applyingQuoteResult;
    private string? _activePromoCode;
```

Убедиться, что есть `using VvCash.Services.Api;` (есть) и `using VvCash.Models.Api;` (есть).

В сигнатуру конструктора добавить параметры (в конец, перед `HttpClient httpClient` или после — порядок не важен, но добавь явно):

```csharp
        IQuoteService quoteService,
        ISessionContext session,
```

И присвоить в теле конструктора:

```csharp
        _quoteService = quoteService;
        _session = session;
```

- [ ] **Step 2: Добавить методы перезапроса quote**

Добавить в класс (например, рядом с `OnCartChanged`):

```csharp
    private void TriggerRequote() => _ = RequoteDebouncedAsync();

    private async Task RequoteDebouncedAsync()
    {
        _quoteCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        _quoteCts = cts;
        try { await Task.Delay(300, cts.Token); }
        catch (TaskCanceledException) { return; }
        await RequoteAsync(cts.Token);
    }

    private async Task RequoteAsync(System.Threading.CancellationToken ct)
    {
        var cardId = SelectedCustomer?.DiscountCard?.Identifier;
        var hasInput = !string.IsNullOrWhiteSpace(cardId) || !string.IsNullOrWhiteSpace(_activePromoCode);

        if (!IsSystemOnline || !hasInput || _cartService.Items.Count == 0 || string.IsNullOrWhiteSpace(_session.WarehouseId))
        {
            ApplyQuoteGuarded(() => _cartService.ClearQuote());
            return;
        }

        var request = QuoteRequestBuilder.Build(_cartService.Items, _session.WarehouseId!, cardId, _activePromoCode);
        var result = await _quoteService.QuoteAsync(request, ct);
        if (ct.IsCancellationRequested) return;

        if (result == null)
        {
            // Сеть-фейл/офлайн: fallback на плоский %.
            ApplyQuoteGuarded(() => _cartService.ClearQuote());
            return;
        }

        ApplyQuoteGuarded(() => _cartService.ApplyQuote(result));

        if (result.Rejected.Count > 0)
        {
            StatusMessage = $"Промокод отклонён: {result.Rejected[0].Reason}";
            _activePromoCode = null;
        }
        else if (!string.IsNullOrWhiteSpace(_activePromoCode) && result.Applied.Count > 0)
        {
            StatusMessage = "Промокод применён";
        }
    }

    // Гард против рекурсии: ApplyQuote/ClearQuote бросают CartChanged ->
    // OnCartChanged не должен снова запускать requote.
    private void ApplyQuoteGuarded(System.Action apply)
    {
        _applyingQuoteResult = true;
        try { apply(); }
        finally { _applyingQuoteResult = false; }
    }
```

- [ ] **Step 3: Запустить requote из OnCartChanged (без рекурсии)**

В конце метода `OnCartChanged` (после `TotalDiscount`/`TotalAmount` присвоений) добавить:

```csharp
        if (!_applyingQuoteResult)
            TriggerRequote();
```

- [ ] **Step 4: Перезапрос при выборе/сбросе клиента**

В обработчике выбора клиента (где сейчас `_cartService.SetCustomerDiscount(result.DiscountCard.Discount)`), оставить плоский % как офлайн-fallback, но добавить запуск requote. После установки `SelectedCustomer = result;` блок заменить на:

```csharp
                    SelectedCustomer = result;
                    if (result.DiscountCard != null && result.DiscountCard.Discount > 0)
                    {
                        _cartService.SetCustomerDiscount(result.DiscountCard.Discount); // офлайн-fallback
                        StatusMessage = $"Клиент: {result.FullName} • Скидка по карте: {result.DiscountCard.Discount}%";
                    }
                    else
                    {
                        _cartService.ClearCustomerDiscount();
                        StatusMessage = $"Выбран клиент: {result.FullName}";
                    }
                    TriggerRequote();
```

В `ClearSelectedCustomer` после `_cartService.ClearCustomerDiscount();` добавить `TriggerRequote();`.

- [ ] **Step 5: Промокод через quote (заменить мок ApplyCoupon)**

Заменить тело команды `ApplyCoupon`:

```csharp
    [RelayCommand]
    private Task ApplyCoupon()
    {
        if (string.IsNullOrWhiteSpace(CouponCode)) return Task.CompletedTask;
        _activePromoCode = CouponCode.Trim();
        StatusMessage = $"Проверка кода: {_activePromoCode}…";
        CouponCode = string.Empty;
        TriggerRequote();
        return Task.CompletedTask;
    }
```

Заменить тело `RemoveCoupon`:

```csharp
    [RelayCommand]
    private void RemoveCoupon(string code)
    {
        _activePromoCode = null;
        _cartService.RemoveCoupon(code);
        TriggerRequote();
    }
```

> `_discountService` больше не используется в этих командах. Поле/инъекцию `IDiscountService`
> можно оставить (не ломает сборку) — удаление мок-сервиса вне объёма этого таска.

- [ ] **Step 6: Маппинг чека через QuoteLineResolver**

В построении `DocumentRequest.Products` (около `Products = _cartService.Items.Select(...)`) заменить инлайн-маппинг на резолвер:

```csharp
                        Products = _cartService.Items.Select(item =>
                        {
                            var (pct, before) = QuoteLineResolver.Resolve(_cartService.Quote, item);
                            return new DocumentProduct
                            {
                                Name = item.Product.Name,
                                ProductId = item.Product.Id,
                                Quantity = item.Quantity,
                                SellPrice = item.Product.Price,
                                PriceBeforeDiscount = before,
                                DiscountPercent = pct
                            };
                        }).ToList()
```

- [ ] **Step 7: Сброс промокода при очистке/паркинге**

В `ClearCart()` (VM-метод, тот что зовёт `_cartService.ClearCart()`), и в park-обработчиках где `SelectedCustomer = null;`, добавить `_activePromoCode = null;`. После восстановления parked sale (`LoadSnapshot`) добавить `TriggerRequote();` (онлайн перезапросит, офлайн оставит плоский %).

- [ ] **Step 8: Сборка и полный прогон**

Run: `pwsh ./run-tests.ps1`
Expected: PASS — все тесты (новые + существующие). Сборка приложения зелёная.

- [ ] **Step 9: Коммит**

```bash
git add src/VvCash/ViewModels/PosViewModel.cs
git commit -m "feat: POS requotes discounts via server, maps per-line to receipt"
```

---

## Task 9: Ручная верификация (интеграция)

- [ ] **Step 1: Билд-проверка без лока**

Run: `pwsh ./run_build.sh` или `dotnet build src/VvCash/VvCash.csproj -o build/verify`
Expected: успешная сборка.

- [ ] **Step 2: Прогон приложения (skill `run` / `verify`)**
  - Открыть смену → в логах `[ShiftService]` подтвердить, что `SessionContext.WarehouseId` заполнен (Task 3 Step 6). Если поле иное — поправить `ExtractWarehouseId`.
  - Онлайн: выбрать клиента с картой → корзина → проверить, что скидка пришла из quote (per-line, не плоский %), `applied` отражён.
  - Ввести промокод → accepted показывает «Промокод применён», invalid → причину из `rejected`.
  - Ручная скидка кассира → суммируется поверх quote, итог не уходит ниже 0.
  - Офлайн (выключить сеть / `IsSystemOnline=false`) → скидка падает на кэшированный плоский % карты, продажа проходит.
  - Провести продажу → проверить чек: `discount_percent`/`price_before_discount` per-line соответствуют quote.

- [ ] **Step 3: ⚠️ Открытый пункт — quote_id в документе**
  Проверить, требует ли бэкенд `quote_id` в теле документа для honor price-locked снапшота
  (сравнить итог сервера и присланный). Если да:
  - добавить `[JsonPropertyName("quote_id")] public string? QuoteId` в `DocumentRequest`
    (`src/VvCash/Models/Api/DocumentRequest.cs`),
  - присвоить `QuoteId = _cartService.QuoteId` при построении запроса,
  - коммит `feat: attach quote_id to sale document`.

---

## Self-Review (выполнено автором плана)

**Покрытие спеки:**
- Полная интеграция `/discounts/quote/` → Tasks 1,2,5,8 ✔
- Офлайн fallback на плоский % → Task 4 (ветка `Quote==null`) + Task 8 (online-гейт/`result==null`) ✔
- Ручная скидка сверху + кламп → Task 4 ✔
- Промокоды через `code`, мок убран из пути → Task 8 Step 5 ✔
- warehouse_id из cash-сессии → Task 3 ✔ (имя поля — открытый пункт, Task 3 Step 6)
- Маппинг чека per-line → Task 6 + Task 8 Step 6 ✔
- quote_id в документе → открытый пункт, Task 9 Step 3 ✔
- Обработка ошибок (quote-фейл/rejected/гонка) → Task 2 (null), Task 8 (CancellationToken, статус) ✔
- Тесты CartService/маппинг/десериализация/оффлайн → Tasks 1,2,4,5,6 ✔

**Плейсхолдеры:** нет — весь код приведён. Два ⚠️ — это явные runtime-проверки против живого бэкенда, не TODO в коде.

**Согласованность типов:** `QuoteResult.DiscountTotal`, `Lines[].DiscountPercent/UnitPrice/ProductId`, `ApplyQuote/ClearQuote/Quote/QuoteId`, `QuoteRequestBuilder.Build`, `QuoteLineResolver.Resolve`, `ISessionContext.WarehouseId`, `ShiftService.ExtractWarehouseId` — имена единообразны во всех тасках.
