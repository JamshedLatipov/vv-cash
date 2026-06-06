# Product Returns Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a per-line product-return flow: browse the sales (expense) list, pick returnable lines with a quantity, and POST a return to the backend, with configurable cash-drawer + return-receipt printing.

**Architecture:** New `ReturnService` (HTTP, routed through the existing `AuthHeaderHandler` so `Cash-Authorization` + `Bearer` headers attach automatically) backs a `ReturnsViewModel` shown in a new master-detail `ReturnsWindow`, opened from a PosView nav button. Post-return cash-drawer/print actions are added to `IPrinterService` and gated by two new settings flags. Online-only; no offline queue.

**Tech Stack:** .NET 10, Avalonia 11.2 (reflection bindings — `AvaloniaUseCompiledBindingsByDefault=false`), CommunityToolkit.Mvvm 8.3, System.Net.Http.Json, xUnit (new test project).

---

## Conventions & Gotchas (read once)

- **Build lock:** the app holds a lock on `VvCash.dll` while running. To verify a build without closing the app: `dotnet build src/VvCash/VvCash.csproj -o build/verify`. `dotnet test` builds the referenced app project to its normal `bin/` — **close the running app before `dotnet test`** or it hits the same lock.
- **Avalonia binding crash:** bindings are reflection-based. In item templates, bind row commands to the row's own DataContext (`ReturnLineVm`), and bind window-level commands via `$parent[ListBox].DataContext.XxxCommand` (the pattern used in `ParkedSalesWindow.axaml`). Never cast an ancestor DataContext to a concrete VM type in a template — compiles, crashes at runtime.
- **Commit hygiene:** `main` often carries unrelated WIP. We work on branch `feature/product-returns` (already created). Stage only the files each task lists — never `git add -A`.
- **Success convention:** envelope responses are success when `status == 0` (matches `ExpenseDocumentService`).

## File Structure

| File | Responsibility | Action |
|---|---|---|
| `tests/VvCash.Tests/VvCash.Tests.csproj` | xUnit test project | Create |
| `tests/VvCash.Tests/StubHttpMessageHandler.cs` | canned-response handler for service tests | Create |
| `src/VvCash/Services/SettingsService.cs` | add 2 flags to `SettingsData` + `SettingsService` | Modify |
| `src/VvCash/Services/ISettingsService.cs` | add 2 flags | Modify |
| `src/VvCash/Models/Api/ReturnModels.cs` | API DTOs for list/return-detail/return-request | Create |
| `src/VvCash/Services/Api/IReturnService.cs` | return service contract | Create |
| `src/VvCash/Services/Api/ReturnService.cs` | return service impl | Create |
| `src/VvCash/App.axaml.cs` | DI registration | Modify |
| `src/VvCash/Models/ReturnReceiptLine.cs` | printer-facing return line | Create |
| `src/VvCash/Services/Hardware/IPrinterService.cs` | add drawer + return-receipt methods | Modify |
| `src/VvCash/Services/Hardware/EscPosPrinterService.cs` | implement drawer kick + return receipt | Modify |
| `src/VvCash/Services/Hardware/MockPrinterService.cs` | implement new methods | Modify |
| `src/VvCash/Services/Hardware/CompositePrinterService.cs` | fan out new methods | Modify |
| `src/VvCash/ViewModels/ReturnLineVm.cs` | per-line row VM with clamp | Create |
| `src/VvCash/ViewModels/ReturnsViewModel.cs` | list + detail + submit + post-return | Create |
| `src/VvCash/Views/ReturnsWindow.axaml` (+ `.axaml.cs`) | master-detail window | Create |
| `src/VvCash/ViewModels/PosViewModel.cs` | inject service + `OpenReturns` command | Modify |
| `src/VvCash/Views/PosView.axaml` | nav button | Modify |
| `src/VvCash/Assets/i18n/{en,ru,uz,tg,kk}.json` | new i18n keys | Modify |

---

## Task 1: Test project scaffold

**Files:**
- Create: `tests/VvCash.Tests/VvCash.Tests.csproj`
- Create: `tests/VvCash.Tests/StubHttpMessageHandler.cs`
- Create: `tests/VvCash.Tests/SmokeTest.cs`

- [ ] **Step 1: Create the test project file**

`tests/VvCash.Tests/VvCash.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\VvCash\VvCash.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the reusable stub HTTP handler**

`tests/VvCash.Tests/StubHttpMessageHandler.cs`:
```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VvCash.Tests;

public class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _responder;
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    public StubHttpMessageHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder)
        => _responder = responder;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        if (request.Content != null)
            LastRequestBody = await request.Content.ReadAsStringAsync(ct);
        var (code, body) = _responder(request);
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }
}
```

- [ ] **Step 3: Add a smoke test**

`tests/VvCash.Tests/SmokeTest.cs`:
```csharp
namespace VvCash.Tests;

public class SmokeTest
{
    [Xunit.Fact]
    public void Truth() => Xunit.Assert.True(true);
}
```

- [ ] **Step 4: Run tests (app must be closed)**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: PASS (1 test). If it errors with a file lock on `VvCash.dll`, close the running app and retry.

- [ ] **Step 5: Commit**

```bash
git add tests/VvCash.Tests/VvCash.Tests.csproj tests/VvCash.Tests/StubHttpMessageHandler.cs tests/VvCash.Tests/SmokeTest.cs
git commit -m "test: scaffold VvCash.Tests xUnit project"
```

---

## Task 2: Settings flags for post-return actions

**Files:**
- Modify: `src/VvCash/Services/SettingsService.cs`
- Modify: `src/VvCash/Services/ISettingsService.cs`
- Test: `tests/VvCash.Tests/SettingsDefaultsTest.cs`

- [ ] **Step 1: Write the failing test**

`tests/VvCash.Tests/SettingsDefaultsTest.cs`:
```csharp
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class SettingsDefaultsTest
{
    [Fact]
    public void PostReturnFlags_DefaultToTrue()
    {
        var data = new SettingsData();
        Assert.True(data.ReturnOpenCashDrawer);
        Assert.True(data.ReturnPrintReceipt);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: FAIL — `SettingsData` has no `ReturnOpenCashDrawer`.

- [ ] **Step 3: Add the fields to `SettingsData`**

In `src/VvCash/Services/SettingsService.cs`, inside `SettingsData` (after `Printers`):
```csharp
    public bool ReturnOpenCashDrawer { get; set; } = true;
    public bool ReturnPrintReceipt { get; set; } = true;
```

- [ ] **Step 4: Add the interface members**

In `src/VvCash/Services/ISettingsService.cs`, after `List<PrinterConfig> Printers { get; set; }`:
```csharp
    bool ReturnOpenCashDrawer { get; set; }
    bool ReturnPrintReceipt { get; set; }
```

- [ ] **Step 5: Implement them on `SettingsService`**

In `src/VvCash/Services/SettingsService.cs`, after the `Printers` property:
```csharp
    public bool ReturnOpenCashDrawer
    {
        get => _data.ReturnOpenCashDrawer;
        set => _data.ReturnOpenCashDrawer = value;
    }

    public bool ReturnPrintReceipt
    {
        get => _data.ReturnPrintReceipt;
        set => _data.ReturnPrintReceipt = value;
    }
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/VvCash/Services/SettingsService.cs src/VvCash/Services/ISettingsService.cs tests/VvCash.Tests/SettingsDefaultsTest.cs
git commit -m "feat: add return cash-drawer/print settings flags"
```

---

## Task 3: API models

**Files:**
- Create: `src/VvCash/Models/Api/ReturnModels.cs`
- Test: `tests/VvCash.Tests/ReturnModelsTest.cs`

- [ ] **Step 1: Write the failing test**

`tests/VvCash.Tests/ReturnModelsTest.cs`:
```csharp
using System.Text.Json;
using VvCash.Models.Api;
using Xunit;

namespace VvCash.Tests;

public class ReturnModelsTest
{
    [Fact]
    public void DeserializesExpenseList()
    {
        const string json = """
        {"body":[{"selected_date":"2026-06-06T17:32:55.052Z","created_at":"2026-06-06T17:32:55.074858Z","id":"9abd5223-e6b1-4cc2-9075-fb128e0261cf","state":"PROCESSED","creator":"admin admin","counterparty":"UNDEFINED UNDEFINED","document_number":"9","cost":40,"to_pay":100,"discount":0,"payed":0,"remain":-100}],"page_count":1,"total_items":1,"item_per_page":10}
        """;
        var res = JsonSerializer.Deserialize<ExpenseListResponse>(json)!;
        Assert.Equal(1, res.PageCount);
        Assert.Single(res.Body);
        Assert.Equal("9abd5223-e6b1-4cc2-9075-fb128e0261cf", res.Body[0].Id);
        Assert.Equal("9", res.Body[0].DocumentNumber);
        Assert.Equal(100m, res.Body[0].ToPay);
    }

    [Fact]
    public void DeserializesReturnDetail()
    {
        const string json = """
        {"message":"success","body":{"id":"26f8d6e7-f46d-4431-b23b-8546b07cba54","details":[{"product":{"id":"6034b45e-daf6-4930-9827-a6fc082dd0dd","name":"Luxurious Rubber Salad","barcode":"77191819"},"id":"60a02d71-4f0b-4dd5-87f0-869a5d590d4d","quantity":3,"quantity_returned":1,"sold_price":100,"discount_in_unit":0,"after_discount":100,"discount_in_percent":0}]},"status":0}
        """;
        var res = JsonSerializer.Deserialize<ReturnDetailResponse>(json)!;
        Assert.Equal(0, res.Status);
        var line = Assert.Single(res.Body!.Details);
        Assert.Equal("6034b45e-daf6-4930-9827-a6fc082dd0dd", line.Product!.Id);
        Assert.Equal("Luxurious Rubber Salad", line.Product.Name);
        Assert.Equal(3, line.Quantity);
        Assert.Equal(1, line.QuantityReturned);
        Assert.Equal(100m, line.AfterDiscount);
    }

    [Fact]
    public void SerializesReturnRequest_SnakeCase()
    {
        var req = new ReturnRequest
        {
            SelectedDate = "2026-06-06",
            Details = { new ReturnLineRequest { Product = "p1", Quantity = 2 } }
        };
        var json = JsonSerializer.Serialize(req);
        Assert.Contains("\"selected_date\":\"2026-06-06\"", json);
        Assert.Contains("\"product\":\"p1\"", json);
        Assert.Contains("\"quantity\":2", json);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: FAIL — `ExpenseListResponse` undefined.

- [ ] **Step 3: Create the models**

`src/VvCash/Models/Api/ReturnModels.cs`:
```csharp
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VvCash.Models.Api;

// GET /documents/expense/?page=
public class ExpenseListResponse
{
    [JsonPropertyName("body")] public List<ExpenseListItem> Body { get; set; } = new();
    [JsonPropertyName("page_count")] public int PageCount { get; set; }
    [JsonPropertyName("total_items")] public int TotalItems { get; set; }
    [JsonPropertyName("item_per_page")] public int ItemPerPage { get; set; }
}

public class ExpenseListItem
{
    [JsonPropertyName("selected_date")] public string? SelectedDate { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("creator")] public string? Creator { get; set; }
    [JsonPropertyName("counterparty")] public string? Counterparty { get; set; }
    [JsonPropertyName("document_number")] public string? DocumentNumber { get; set; }
    [JsonPropertyName("cost")] public decimal Cost { get; set; }
    [JsonPropertyName("to_pay")] public decimal ToPay { get; set; }
    [JsonPropertyName("discount")] public decimal Discount { get; set; }
    [JsonPropertyName("payed")] public decimal Payed { get; set; }
    [JsonPropertyName("remain")] public decimal Remain { get; set; }
}

// GET /documents/return/{id}/
public class ReturnDetailResponse
{
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("body")] public ReturnDetailBody? Body { get; set; }
    [JsonPropertyName("status")] public int Status { get; set; }
}

public class ReturnDetailBody
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("details")] public List<ReturnDetailLine> Details { get; set; } = new();
}

public class ReturnDetailLine
{
    [JsonPropertyName("product")] public ReturnProduct? Product { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("quantity")] public int Quantity { get; set; }
    [JsonPropertyName("quantity_returned")] public int QuantityReturned { get; set; }
    [JsonPropertyName("sold_price")] public decimal SoldPrice { get; set; }
    [JsonPropertyName("discount_in_unit")] public decimal DiscountInUnit { get; set; }
    [JsonPropertyName("after_discount")] public decimal AfterDiscount { get; set; }
    [JsonPropertyName("discount_in_percent")] public decimal DiscountInPercent { get; set; }
}

public class ReturnProduct
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("barcode")] public string? Barcode { get; set; }
    [JsonPropertyName("article")] public string? Article { get; set; }
}

// POST /documents/return/{id}/
public class ReturnRequest
{
    [JsonPropertyName("selected_date")] public string SelectedDate { get; set; } = string.Empty;
    [JsonPropertyName("details")] public List<ReturnLineRequest> Details { get; set; } = new();
}

public class ReturnLineRequest
{
    [JsonPropertyName("product")] public string Product { get; set; } = string.Empty;
    [JsonPropertyName("quantity")] public int Quantity { get; set; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: PASS (all 3 model tests).

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Models/Api/ReturnModels.cs tests/VvCash.Tests/ReturnModelsTest.cs
git commit -m "feat: add return API models"
```

---

## Task 4: ReturnService

**Files:**
- Create: `src/VvCash/Services/Api/IReturnService.cs`
- Create: `src/VvCash/Services/Api/ReturnService.cs`
- Test: `tests/VvCash.Tests/ReturnServiceTest.cs`

- [ ] **Step 1: Write the failing test**

`tests/VvCash.Tests/ReturnServiceTest.cs`:
```csharp
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using Xunit;

namespace VvCash.Tests;

public class ReturnServiceTest
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

    private static ReturnService Build(StubHttpMessageHandler handler)
        => new ReturnService(new HttpClient(handler), new FakeSettings());

    [Fact]
    public async Task GetSalesAsync_ParsesAndHitsPageParam()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """{"body":[{"id":"x","document_number":"9","to_pay":100}],"page_count":2,"total_items":15,"item_per_page":10}"""));
        var svc = Build(handler);

        var res = await svc.GetSalesAsync(2);

        Assert.Equal(2, res.PageCount);
        Assert.Equal("x", res.Body[0].Id);
        Assert.Contains("documents/expense/?page=2", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetReturnableLinesAsync_ReturnsBody()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """{"message":"success","body":{"id":"d","details":[{"product":{"id":"p"},"quantity":2,"quantity_returned":0,"after_discount":50}]},"status":0}"""));
        var svc = Build(handler);

        var body = await svc.GetReturnableLinesAsync("doc1");

        Assert.Equal("d", body.Id);
        Assert.Single(body.Details);
        Assert.Contains("documents/return/doc1/", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task CreateReturnAsync_TrueOnStatusZero()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """{"message":"success","body":{},"status":0}"""));
        var svc = Build(handler);

        var ok = await svc.CreateReturnAsync("doc1", new ReturnRequest { SelectedDate = "2026-06-06" });

        Assert.True(ok);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("documents/return/doc1/", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task CreateReturnAsync_FalseOnNonZeroStatus()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """{"message":"error","body":"nope","status":-1}"""));
        var svc = Build(handler);

        var ok = await svc.CreateReturnAsync("doc1", new ReturnRequest { SelectedDate = "2026-06-06" });

        Assert.False(ok);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: FAIL — `IReturnService`/`ReturnService` undefined.

- [ ] **Step 3: Create the interface**

`src/VvCash/Services/Api/IReturnService.cs`:
```csharp
using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public interface IReturnService
{
    Task<ExpenseListResponse> GetSalesAsync(int page = 1);
    Task<ReturnDetailBody> GetReturnableLinesAsync(string expenseId);
    Task<bool> CreateReturnAsync(string expenseId, ReturnRequest request);
}
```

- [ ] **Step 4: Create the implementation**

`src/VvCash/Services/Api/ReturnService.cs`:
```csharp
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public class ReturnService : IReturnService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public ReturnService(HttpClient httpClient, ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
    }

    private string GetBaseUrl()
    {
        var baseUrl = _settingsService.BackendUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("BackendUrl is not configured.");
        if (!baseUrl.EndsWith("/"))
            baseUrl += "/";
        return baseUrl;
    }

    public async Task<ExpenseListResponse> GetSalesAsync(int page = 1)
    {
        var url = $"{GetBaseUrl()}documents/expense/?page={page}";
        var res = await _httpClient.GetFromJsonAsync<ExpenseListResponse>(url);
        return res ?? new ExpenseListResponse();
    }

    public async Task<ReturnDetailBody> GetReturnableLinesAsync(string expenseId)
    {
        var url = $"{GetBaseUrl()}documents/return/{expenseId}/";
        var res = await _httpClient.GetFromJsonAsync<ReturnDetailResponse>(url);
        if (res == null || res.Status != 0 || res.Body == null)
            throw new InvalidOperationException(res?.Message ?? "Failed to load returnable lines.");
        return res.Body;
    }

    public async Task<bool> CreateReturnAsync(string expenseId, ReturnRequest request)
    {
        var url = $"{GetBaseUrl()}documents/return/{expenseId}/";
        var response = await _httpClient.PostAsJsonAsync(url, request);
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            return false;
        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.TryGetProperty("status", out var s) && s.GetInt32() == 0;
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: PASS (4 service tests).

- [ ] **Step 6: Commit**

```bash
git add src/VvCash/Services/Api/IReturnService.cs src/VvCash/Services/Api/ReturnService.cs tests/VvCash.Tests/ReturnServiceTest.cs
git commit -m "feat: add ReturnService for sales list + returns"
```

---

## Task 5: DI registration

**Files:**
- Modify: `src/VvCash/App.axaml.cs:147` (near the other `AddHttpClient` lines)

- [ ] **Step 1: Register the service**

In `src/VvCash/App.axaml.cs`, immediately after the line
`services.AddSingleton<IExpenseDocumentService>(sp => sp.GetRequiredService<ExpenseDocumentService>());`
add:
```csharp
        services.AddHttpClient<IReturnService, ReturnService>().AddHttpMessageHandler<AuthHeaderHandler>();
```

- [ ] **Step 2: Verify build (app may be running → use isolated output)**

Run: `dotnet build src/VvCash/VvCash.csproj -o build/verify`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/VvCash/App.axaml.cs
git commit -m "feat: register ReturnService in DI with AuthHeaderHandler"
```

---

## Task 6: Printer — cash drawer + return receipt

**Files:**
- Create: `src/VvCash/Models/ReturnReceiptLine.cs`
- Modify: `src/VvCash/Services/Hardware/IPrinterService.cs`
- Modify: `src/VvCash/Services/Hardware/EscPosPrinterService.cs`
- Modify: `src/VvCash/Services/Hardware/MockPrinterService.cs`
- Modify: `src/VvCash/Services/Hardware/CompositePrinterService.cs`
- Test: `tests/VvCash.Tests/EscPosReturnTest.cs`

- [ ] **Step 1: Write the failing test**

`tests/VvCash.Tests/EscPosReturnTest.cs`:
```csharp
using System.Collections.Generic;
using System.Text;
using VvCash.Models;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

public class EscPosReturnTest
{
    [Fact]
    public void DrawerKick_IsStandardPulse()
    {
        Assert.Equal(new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA }, EscPosPrinterService.CmdDrawerKick);
    }

    [Fact]
    public void ReturnReceiptBuffer_ContainsHeaderAndTotal()
    {
        var lines = new List<ReturnReceiptLine> { new("Salad", 2, 200m) };
        var bytes = EscPosPrinterService.BuildReturnReceipt(lines, 200m, "9");
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("RETURN", text);
        Assert.Contains("Salad", text);
        Assert.Contains("#9", text);
        Assert.Contains("200", text);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: FAIL — `ReturnReceiptLine` and `EscPosPrinterService.CmdDrawerKick` undefined.

- [ ] **Step 3: Create the printer-facing line model**

`src/VvCash/Models/ReturnReceiptLine.cs`:
```csharp
namespace VvCash.Models;

public record ReturnReceiptLine(string Name, int Quantity, decimal LineRefund);
```

- [ ] **Step 4: Extend the interface**

In `src/VvCash/Services/Hardware/IPrinterService.cs`, add inside the interface:
```csharp
    System.Threading.Tasks.Task<bool> OpenCashDrawerAsync();
    System.Threading.Tasks.Task<bool> PrintReturnReceiptAsync(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> lines,
        decimal totalRefund, string documentNumber);
```

- [ ] **Step 5: Implement on `EscPosPrinterService`**

In `src/VvCash/Services/Hardware/EscPosPrinterService.cs`, add the drawer command beside the other `Cmd*` fields:
```csharp
    public static readonly byte[] CmdDrawerKick = { 0x1B, 0x70, 0x00, 0x19, 0xFA };
```
Then add these methods (before the closing brace of the class):
```csharp
    public static byte[] BuildReturnReceipt(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> lines,
        decimal totalRefund, string documentNumber)
    {
        using var ms = new MemoryStream();
        Write(ms, CmdInit);
        Write(ms, CmdAlignCenter);
        Write(ms, CmdDoubleSizeOn);
        WriteLine(ms, "RETURN / VOZVRAT");
        Write(ms, CmdDoubleSizeOff);
        WriteLine(ms, $"Doc #{documentNumber}");
        WriteLine(ms, "----------------------------");
        Write(ms, CmdAlignLeft);
        foreach (var l in lines)
            WriteLine(ms, PadLine($"{l.Name} x{l.Quantity}", $"{l.LineRefund:F2}", 32));
        WriteLine(ms, "----------------------------");
        Write(ms, CmdBoldOn);
        WriteLine(ms, PadLine("REFUND:", $"{totalRefund:F2}", 32));
        Write(ms, CmdBoldOff);
        Write(ms, CmdLineFeed);
        Write(ms, CmdLineFeed);
        Write(ms, CmdCut);
        return ms.ToArray();
    }

    public async Task<bool> PrintReturnReceiptAsync(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> lines,
        decimal totalRefund, string documentNumber)
    {
        try
        {
            await SendAsync(BuildReturnReceipt(lines, totalRefund, documentNumber));
            return true;
        }
        catch
        {
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }

    public async Task<bool> OpenCashDrawerAsync()
    {
        try
        {
            await SendAsync(CmdDrawerKick);
            return true;
        }
        catch
        {
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }
```

- [ ] **Step 6: Implement on `MockPrinterService`**

In `src/VvCash/Services/Hardware/MockPrinterService.cs`, add before the closing brace:
```csharp
    public Task<bool> OpenCashDrawerAsync()
    {
        Console.WriteLine("[MockPrinter] Cash drawer kick");
        return Task.FromResult(true);
    }

    public Task<bool> PrintReturnReceiptAsync(
        IEnumerable<VvCash.Models.ReturnReceiptLine> lines, decimal totalRefund, string documentNumber)
    {
        Console.WriteLine($"=== RETURN #{documentNumber} ===");
        foreach (var l in lines)
            Console.WriteLine($"  {l.Name} x{l.Quantity}  {l.LineRefund:F2}");
        Console.WriteLine($"REFUND: {totalRefund:F2}");
        Console.WriteLine("===============");
        return Task.FromResult(true);
    }
```

- [ ] **Step 7: Implement on `CompositePrinterService`**

In `src/VvCash/Services/Hardware/CompositePrinterService.cs`, add before the closing brace:
```csharp
    public async Task<bool> OpenCashDrawerAsync()
    {
        if (!_printers.Any()) return false;
        var tasks = _printers.Select(p => p.OpenCashDrawerAsync()).ToList();
        await Task.WhenAll(tasks);
        return tasks.Any(t => t.Result);
    }

    public async Task<bool> PrintReturnReceiptAsync(
        IEnumerable<VvCash.Models.ReturnReceiptLine> lines, decimal totalRefund, string documentNumber)
    {
        if (!_printers.Any()) return false;
        var list = lines.ToList();
        var tasks = _printers.Select(p => p.PrintReturnReceiptAsync(list, totalRefund, documentNumber)).ToList();
        await Task.WhenAll(tasks);
        return tasks.Any(t => t.Result);
    }
```

- [ ] **Step 8: Run test to verify it passes**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: PASS (2 printer tests).

- [ ] **Step 9: Commit**

```bash
git add src/VvCash/Models/ReturnReceiptLine.cs src/VvCash/Services/Hardware/IPrinterService.cs src/VvCash/Services/Hardware/EscPosPrinterService.cs src/VvCash/Services/Hardware/MockPrinterService.cs src/VvCash/Services/Hardware/CompositePrinterService.cs tests/VvCash.Tests/EscPosReturnTest.cs
git commit -m "feat: add cash-drawer kick and return-receipt printing"
```

---

## Task 7: ReturnLineVm (row VM with clamp)

**Files:**
- Create: `src/VvCash/ViewModels/ReturnLineVm.cs`
- Test: `tests/VvCash.Tests/ReturnLineVmTest.cs`

- [ ] **Step 1: Write the failing test**

`tests/VvCash.Tests/ReturnLineVmTest.cs`:
```csharp
using VvCash.Models.Api;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

public class ReturnLineVmTest
{
    private static ReturnLineVm Make(int qty, int returned, decimal after) =>
        new(new ReturnDetailLine
        {
            Product = new ReturnProduct { Id = "p", Name = "Salad" },
            Quantity = qty, QuantityReturned = returned, AfterDiscount = after
        });

    [Fact]
    public void MaxReturnable_IsSoldMinusReturned()
    {
        var vm = Make(3, 1, 50m);
        Assert.Equal(2, vm.MaxReturnable);
        Assert.True(vm.IsReturnable);
    }

    [Fact]
    public void ReturnQty_ClampsToRange()
    {
        var vm = Make(3, 1, 50m); // max 2
        vm.ReturnQty = 5;
        Assert.Equal(2, vm.ReturnQty);
        vm.ReturnQty = -4;
        Assert.Equal(0, vm.ReturnQty);
    }

    [Fact]
    public void LineRefund_IsQtyTimesUnitPrice()
    {
        var vm = Make(3, 0, 50m);
        vm.ReturnQty = 2;
        Assert.Equal(100m, vm.LineRefund);
    }

    [Fact]
    public void FullyReturned_NotReturnable()
    {
        var vm = Make(1, 1, 50m);
        Assert.Equal(0, vm.MaxReturnable);
        Assert.False(vm.IsReturnable);
        vm.ReturnQty = 1;
        Assert.Equal(0, vm.ReturnQty);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: FAIL — `ReturnLineVm` undefined.

- [ ] **Step 3: Create the row VM**

`src/VvCash/ViewModels/ReturnLineVm.cs`:
```csharp
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Models.Api;

namespace VvCash.ViewModels;

public partial class ReturnLineVm : ObservableObject
{
    public string ProductId { get; }
    public string Name { get; }
    public string? Barcode { get; }
    public int SoldQty { get; }
    public int AlreadyReturned { get; }
    public int MaxReturnable { get; }
    public decimal UnitPrice { get; }
    public bool IsReturnable => MaxReturnable > 0;

    public event Action? RefundChanged;

    private int _returnQty;
    public int ReturnQty
    {
        get => _returnQty;
        set
        {
            var clamped = value < 0 ? 0 : (value > MaxReturnable ? MaxReturnable : value);
            if (SetProperty(ref _returnQty, clamped))
            {
                OnPropertyChanged(nameof(LineRefund));
                RefundChanged?.Invoke();
            }
        }
    }

    public decimal LineRefund => ReturnQty * UnitPrice;

    public ReturnLineVm(ReturnDetailLine line)
    {
        ProductId = line.Product?.Id ?? string.Empty;
        Name = line.Product?.Name ?? string.Empty;
        Barcode = line.Product?.Barcode;
        SoldQty = line.Quantity;
        AlreadyReturned = line.QuantityReturned;
        MaxReturnable = Math.Max(0, line.Quantity - line.QuantityReturned);
        UnitPrice = line.AfterDiscount;
    }

    [RelayCommand]
    private void Increment() => ReturnQty += 1;

    [RelayCommand]
    private void Decrement() => ReturnQty -= 1;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: PASS (4 row-VM tests).

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/ViewModels/ReturnLineVm.cs tests/VvCash.Tests/ReturnLineVmTest.cs
git commit -m "feat: add ReturnLineVm with clamped return quantity"
```

---

## Task 8: ReturnsViewModel

**Files:**
- Create: `src/VvCash/ViewModels/ReturnsViewModel.cs`
- Test: `tests/VvCash.Tests/ReturnsViewModelTest.cs`

> The submit path uses a real `IReturnService` (faked in tests) and `IPrinterService`. We test request-building and total math by exposing a testable `BuildRequest()` helper and `TotalRefund`. The window dependency is `Avalonia.Controls.Window?` and is nullable so tests can pass `null`.

- [ ] **Step 1: Write the failing test**

`tests/VvCash.Tests/ReturnsViewModelTest.cs`:
```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using VvCash.Services.Hardware;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

public class ReturnsViewModelTest
{
    private sealed class FakeReturnService : IReturnService
    {
        public ReturnRequest? LastRequest;
        public string? LastExpenseId;
        public Task<ExpenseListResponse> GetSalesAsync(int page = 1)
            => Task.FromResult(new ExpenseListResponse());
        public Task<ReturnDetailBody> GetReturnableLinesAsync(string expenseId)
            => Task.FromResult(new ReturnDetailBody());
        public Task<bool> CreateReturnAsync(string expenseId, ReturnRequest request)
        {
            LastExpenseId = expenseId; LastRequest = request;
            return Task.FromResult(true);
        }
    }

    private sealed class CountingPrinter : IPrinterService
    {
        public int Drawer; public int Receipt;
        public PrinterStatus Status => PrinterStatus.Ready;
        public event System.EventHandler<PrinterStatus>? StatusChanged;
        public Task<bool> PrintReceiptAsync(IEnumerable<CartItem> i, decimal s, decimal d, decimal t, IEnumerable<Coupon> c) => Task.FromResult(true);
        public Task<bool> PrintPreReceiptAsync(IEnumerable<CartItem> i, decimal t) => Task.FromResult(true);
        public Task<bool> OpenCashDrawerAsync() { Drawer++; return Task.FromResult(true); }
        public Task<bool> PrintReturnReceiptAsync(IEnumerable<ReturnReceiptLine> l, decimal t, string d) { Receipt++; return Task.FromResult(true); }
    }

    private sealed class FakeSettings : ISettingsService
    {
        public string BackendUrl { get; set; } = "https://x/";
        public string CashRegisterToken { get; set; } = "";
        public string AuthToken { get; set; } = "";
        public System.DateTime? AuthTokenExpiresAt { get; set; }
        public int SyncIntervalMinutes { get; set; } = 10;
        public string Language { get; set; } = "ru";
        public List<PrinterConfig> Printers { get; set; } = new();
        public bool ReturnOpenCashDrawer { get; set; } = true;
        public bool ReturnPrintReceipt { get; set; } = true;
        public event System.EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, System.EventArgs.Empty);
    }

    private static ReturnsViewModel Build(FakeReturnService svc, CountingPrinter printer, FakeSettings settings)
    {
        var vm = new ReturnsViewModel(null, svc, printer, settings);
        vm.SelectedSale = new ExpenseListItem
        {
            Id = "doc1", DocumentNumber = "9", SelectedDate = "2026-06-06T17:32:55.052Z"
        };
        vm.Lines.Add(new ReturnLineVm(new ReturnDetailLine
        { Product = new ReturnProduct { Id = "pA" }, Quantity = 3, QuantityReturned = 0, AfterDiscount = 50 }));
        vm.Lines.Add(new ReturnLineVm(new ReturnDetailLine
        { Product = new ReturnProduct { Id = "pB" }, Quantity = 2, QuantityReturned = 0, AfterDiscount = 10 }));
        return vm;
    }

    [Fact]
    public void BuildRequest_OnlyIncludesSelectedLines_WithDateOnly()
    {
        var vm = Build(new FakeReturnService(), new CountingPrinter(), new FakeSettings());
        vm.Lines[0].ReturnQty = 2; // pA only
        var req = vm.BuildRequest();
        Assert.Equal("2026-06-06", req.SelectedDate);
        var d = Assert.Single(req.Details);
        Assert.Equal("pA", d.Product);
        Assert.Equal(2, d.Quantity);
    }

    [Fact]
    public void TotalRefund_SumsSelectedLines()
    {
        var vm = Build(new FakeReturnService(), new CountingPrinter(), new FakeSettings());
        vm.Lines[0].ReturnQty = 2;  // 100
        vm.Lines[1].ReturnQty = 1;  // 10
        Assert.Equal(110m, vm.TotalRefund);
        Assert.True(vm.CanSubmit);
    }

    [Fact]
    public async Task Submit_PostsAndRunsConfiguredPostActions()
    {
        var svc = new FakeReturnService();
        var printer = new CountingPrinter();
        var vm = Build(svc, printer, new FakeSettings());
        vm.Lines[0].ReturnQty = 1;

        await vm.SubmitReturnCommand.ExecuteAsync(null);

        Assert.Equal("doc1", svc.LastExpenseId);
        Assert.Equal(1, printer.Drawer);
        Assert.Equal(1, printer.Receipt);
    }

    [Fact]
    public async Task Submit_RespectsDisabledPostActions()
    {
        var svc = new FakeReturnService();
        var printer = new CountingPrinter();
        var settings = new FakeSettings { ReturnOpenCashDrawer = false, ReturnPrintReceipt = false };
        var vm = Build(svc, printer, settings);
        vm.Lines[0].ReturnQty = 1;

        await vm.SubmitReturnCommand.ExecuteAsync(null);

        Assert.Equal(0, printer.Drawer);
        Assert.Equal(0, printer.Receipt);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: FAIL — `ReturnsViewModel` undefined.

- [ ] **Step 3: Create the ViewModel**

`src/VvCash/ViewModels/ReturnsViewModel.cs`:
```csharp
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using VvCash.Services.Hardware;

namespace VvCash.ViewModels;

public partial class ReturnsViewModel : ViewModelBase
{
    private readonly Window? _window;
    private readonly IReturnService _returnService;
    private readonly IPrinterService _printerService;
    private readonly ISettingsService _settingsService;

    [ObservableProperty] private ObservableCollection<ExpenseListItem> _sales = new();
    [ObservableProperty] private ObservableCollection<ReturnLineVm> _lines = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSale))]
    private ExpenseListItem? _selectedSale;

    [ObservableProperty] private bool _isLoadingSales;
    [ObservableProperty] private bool _isLoadingLines;
    [ObservableProperty] private bool _isSubmitting;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _successMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMorePages))]
    private int _currentPage = 1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMorePages))]
    private int _pageCount = 1;

    public bool HasSelectedSale => SelectedSale != null;
    public bool HasMorePages => CurrentPage < PageCount;
    public decimal TotalRefund => Lines.Sum(l => l.LineRefund);
    public bool CanSubmit => !IsSubmitting && Lines.Any(l => l.ReturnQty > 0);

    public ReturnsViewModel(Window? window, IReturnService returnService,
        IPrinterService printerService, ISettingsService settingsService)
    {
        _window = window;
        _returnService = returnService;
        _printerService = printerService;
        _settingsService = settingsService;
        if (window != null)
            _ = LoadSalesAsync();
    }

    private async Task LoadSalesAsync()
    {
        IsLoadingSales = true;
        ErrorMessage = null;
        try
        {
            var res = await _returnService.GetSalesAsync(CurrentPage);
            Sales = new ObservableCollection<ExpenseListItem>(res.Body);
            PageCount = Math.Max(1, res.PageCount);
        }
        catch (Exception)
        {
            ErrorMessage = I18nService.Instance["NoConnection"];
        }
        finally
        {
            IsLoadingSales = false;
        }
    }

    partial void OnSelectedSaleChanged(ExpenseListItem? value)
    {
        if (value != null)
            _ = LoadLinesAsync(value.Id);
        else
            SetLines(Array.Empty<ReturnLineVm>());
    }

    private async Task LoadLinesAsync(string expenseId)
    {
        IsLoadingLines = true;
        ErrorMessage = null;
        SuccessMessage = null;
        try
        {
            var body = await _returnService.GetReturnableLinesAsync(expenseId);
            SetLines(body.Details.Select(d => new ReturnLineVm(d)));
        }
        catch (Exception)
        {
            ErrorMessage = I18nService.Instance["ReturnFailed"];
            SetLines(Array.Empty<ReturnLineVm>());
        }
        finally
        {
            IsLoadingLines = false;
        }
    }

    private void SetLines(System.Collections.Generic.IEnumerable<ReturnLineVm> items)
    {
        foreach (var l in Lines) l.RefundChanged -= OnLineRefundChanged;
        Lines = new ObservableCollection<ReturnLineVm>(items);
        foreach (var l in Lines) l.RefundChanged += OnLineRefundChanged;
        OnPropertyChanged(nameof(TotalRefund));
        OnPropertyChanged(nameof(CanSubmit));
    }

    private void OnLineRefundChanged()
    {
        OnPropertyChanged(nameof(TotalRefund));
        OnPropertyChanged(nameof(CanSubmit));
    }

    public ReturnRequest BuildRequest()
    {
        var date = SelectedSale?.SelectedDate;
        var dateOnly = DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : (date ?? string.Empty);
        return new ReturnRequest
        {
            SelectedDate = dateOnly,
            Details = Lines.Where(l => l.ReturnQty > 0)
                .Select(l => new ReturnLineRequest { Product = l.ProductId, Quantity = l.ReturnQty })
                .ToList()
        };
    }

    [RelayCommand]
    private async Task SubmitReturn()
    {
        if (SelectedSale == null || !CanSubmit) return;
        IsSubmitting = true;
        ErrorMessage = null;
        SuccessMessage = null;
        try
        {
            var request = BuildRequest();
            var ok = await _returnService.CreateReturnAsync(SelectedSale.Id, request);
            if (!ok)
            {
                ErrorMessage = I18nService.Instance["ReturnFailed"];
                return;
            }

            await RunPostReturnActionsAsync(SelectedSale.DocumentNumber ?? string.Empty);
            SuccessMessage = I18nService.Instance["ReturnSuccess"];
            await LoadLinesAsync(SelectedSale.Id);
        }
        catch (Exception)
        {
            ErrorMessage = I18nService.Instance["NoConnection"];
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private async Task RunPostReturnActionsAsync(string documentNumber)
    {
        if (_settingsService.ReturnOpenCashDrawer)
        {
            try { await _printerService.OpenCashDrawerAsync(); } catch { }
        }
        if (_settingsService.ReturnPrintReceipt)
        {
            var receiptLines = Lines.Where(l => l.ReturnQty > 0)
                .Select(l => new ReturnReceiptLine(l.Name, l.ReturnQty, l.LineRefund));
            try { await _printerService.PrintReturnReceiptAsync(receiptLines, TotalRefund, documentNumber); }
            catch { }
        }
    }

    [RelayCommand]
    private async Task NextPage()
    {
        if (!HasMorePages) return;
        CurrentPage++;
        await LoadSalesAsync();
    }

    [RelayCommand]
    private async Task PrevPage()
    {
        if (CurrentPage <= 1) return;
        CurrentPage--;
        await LoadSalesAsync();
    }

    [RelayCommand]
    private void Close() => _window?.Close();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: PASS (4 VM tests).

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/ViewModels/ReturnsViewModel.cs tests/VvCash.Tests/ReturnsViewModelTest.cs
git commit -m "feat: add ReturnsViewModel with submit and post-return actions"
```

---

## Task 9: ReturnsWindow view

**Files:**
- Create: `src/VvCash/Views/ReturnsWindow.axaml`
- Create: `src/VvCash/Views/ReturnsWindow.axaml.cs`

- [ ] **Step 1: Create the window XAML**

`src/VvCash/Views/ReturnsWindow.axaml`:
```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:services="clr-namespace:VvCash.Services"
        xmlns:vm="using:VvCash.ViewModels"
        xmlns:models="using:VvCash.Models.Api"
        xmlns:material="using:Material.Icons.Avalonia"
        x:Class="VvCash.Views.ReturnsWindow"
        x:DataType="vm:ReturnsViewModel"
        Title="Returns"
        Width="980" Height="640"
        WindowStartupLocation="CenterOwner"
        Background="{StaticResource BackgroundBrush}"
        CornerRadius="12"
        ExtendClientAreaToDecorationsHint="True"
        ExtendClientAreaChromeHints="NoChrome"
        ExtendClientAreaTitleBarHeightHint="-1">

  <Border Background="White" CornerRadius="12" ClipToBounds="True" BoxShadow="0 20 25 -5 #40000000">
    <Grid RowDefinitions="Auto,*,Auto">

      <!-- Header -->
      <Border Grid.Row="0" BorderBrush="{StaticResource Slate100Brush}" BorderThickness="0,0,0,1" Padding="32,24">
        <Grid ColumnDefinitions="*,Auto">
          <StackPanel Grid.Column="0" Orientation="Horizontal" Spacing="12">
            <material:MaterialIcon Kind="KeyboardReturn" Width="36" Height="36" Foreground="{StaticResource PrimaryBrush}"/>
            <TextBlock Text="{Binding [Returns], Source={x:Static services:I18nService.Instance}}" FontSize="28" FontWeight="Bold" Foreground="{StaticResource Slate900Brush}" VerticalAlignment="Center"/>
          </StackPanel>
          <Button Grid.Column="1" Classes="Transparent" Command="{Binding CloseCommand}" Padding="8" CornerRadius="99" Background="Transparent">
            <material:MaterialIcon Kind="Close" Width="24" Height="24" Foreground="{StaticResource Slate500Brush}"/>
          </Button>
        </Grid>
      </Border>

      <!-- Body: sales list | return lines -->
      <Grid Grid.Row="1" ColumnDefinitions="380,*">

        <!-- Left: sales -->
        <Grid Grid.Column="0" RowDefinitions="*,Auto" Margin="24,16">
          <ListBox Grid.Row="0" ItemsSource="{Binding Sales}" SelectedItem="{Binding SelectedSale}" Background="Transparent">
            <ListBox.ItemTemplate>
              <DataTemplate DataType="models:ExpenseListItem">
                <Border Background="{StaticResource Slate50Brush}" BorderBrush="{StaticResource Slate200Brush}" BorderThickness="1" CornerRadius="8" Padding="14" Margin="0,4">
                  <Grid ColumnDefinitions="*,Auto">
                    <StackPanel Grid.Column="0">
                      <TextBlock FontSize="15" FontWeight="Bold" Foreground="{StaticResource Slate900Brush}">
                        <Run Text="#"/><Run Text="{Binding DocumentNumber}"/>
                      </TextBlock>
                      <TextBlock Text="{Binding Creator}" FontSize="12" Foreground="{StaticResource Slate600Brush}"/>
                      <TextBlock Text="{Binding SelectedDate}" FontSize="11" Foreground="{StaticResource Slate500Brush}"/>
                    </StackPanel>
                    <TextBlock Grid.Column="1" Text="{Binding ToPay, StringFormat='{}{0:N2}'}" FontSize="15" FontWeight="SemiBold" Foreground="{StaticResource PrimaryBrush}" VerticalAlignment="Center"/>
                  </Grid>
                </Border>
              </DataTemplate>
            </ListBox.ItemTemplate>
          </ListBox>
          <TextBlock Grid.Row="0" Text="{Binding [NoSales], Source={x:Static services:I18nService.Instance}}" IsVisible="{Binding !Sales.Count}" HorizontalAlignment="Center" VerticalAlignment="Center" FontSize="15" Foreground="{StaticResource Slate400Brush}"/>
          <StackPanel Grid.Row="1" Orientation="Horizontal" HorizontalAlignment="Center" Spacing="12" Margin="0,12,0,0">
            <Button Classes="OutlinedButton" Command="{Binding PrevPageCommand}" Content="{Binding [Prev], Source={x:Static services:I18nService.Instance}}"/>
            <TextBlock VerticalAlignment="Center" Foreground="{StaticResource Slate600Brush}">
              <Run Text="{Binding CurrentPage}"/><Run Text=" / "/><Run Text="{Binding PageCount}"/>
            </TextBlock>
            <Button Classes="OutlinedButton" Command="{Binding NextPageCommand}" Content="{Binding [Next], Source={x:Static services:I18nService.Instance}}"/>
          </StackPanel>
        </Grid>

        <!-- Right: returnable lines -->
        <Border Grid.Column="1" BorderBrush="{StaticResource Slate100Brush}" BorderThickness="1,0,0,0" Padding="24,16">
          <Grid>
            <TextBlock Text="{Binding [SelectSaleToReturn], Source={x:Static services:I18nService.Instance}}" IsVisible="{Binding !HasSelectedSale}" HorizontalAlignment="Center" VerticalAlignment="Center" FontSize="16" Foreground="{StaticResource Slate400Brush}"/>
            <ItemsControl ItemsSource="{Binding Lines}" IsVisible="{Binding HasSelectedSale}">
              <ItemsControl.ItemTemplate>
                <DataTemplate DataType="vm:ReturnLineVm">
                  <Border Background="{StaticResource Slate50Brush}" BorderBrush="{StaticResource Slate200Brush}" BorderThickness="1" CornerRadius="8" Padding="14" Margin="0,4" IsEnabled="{Binding IsReturnable}">
                    <Grid ColumnDefinitions="*,Auto,Auto">
                      <StackPanel Grid.Column="0" VerticalAlignment="Center">
                        <TextBlock Text="{Binding Name}" FontSize="15" FontWeight="Bold" Foreground="{StaticResource Slate900Brush}"/>
                        <TextBlock FontSize="12" Foreground="{StaticResource Slate500Brush}">
                          <Run Text="{Binding [SoldQty], Source={x:Static services:I18nService.Instance}}"/><Run Text=": "/><Run Text="{Binding SoldQty}"/>
                          <Run Text="  •  "/>
                          <Run Text="{Binding [AlreadyReturned], Source={x:Static services:I18nService.Instance}}"/><Run Text=": "/><Run Text="{Binding AlreadyReturned}"/>
                        </TextBlock>
                      </StackPanel>
                      <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="8" VerticalAlignment="Center" Margin="0,0,16,0">
                        <Button Classes="OutlinedButton" Command="{Binding DecrementCommand}" Padding="8,2"><TextBlock Text="−"/></Button>
                        <TextBlock Text="{Binding ReturnQty}" FontSize="16" FontWeight="Bold" MinWidth="28" TextAlignment="Center" VerticalAlignment="Center"/>
                        <Button Classes="OutlinedButton" Command="{Binding IncrementCommand}" Padding="8,2"><TextBlock Text="+"/></Button>
                      </StackPanel>
                      <TextBlock Grid.Column="2" Text="{Binding LineRefund, StringFormat='{}{0:N2}'}" FontSize="15" FontWeight="SemiBold" Foreground="{StaticResource PrimaryBrush}" VerticalAlignment="Center" MinWidth="70" TextAlignment="Right"/>
                    </Grid>
                  </Border>
                </DataTemplate>
              </ItemsControl.ItemTemplate>
            </ItemsControl>
          </Grid>
        </Border>
      </Grid>

      <!-- Footer -->
      <Border Grid.Row="2" Background="White" BorderBrush="{StaticResource Slate100Brush}" BorderThickness="0,1,0,0" Padding="32,20">
        <Grid ColumnDefinitions="*,Auto">
          <StackPanel Grid.Column="0" VerticalAlignment="Center">
            <TextBlock Text="{Binding ErrorMessage}" Foreground="{StaticResource Red500Brush}" IsVisible="{Binding ErrorMessage, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>
            <TextBlock Text="{Binding SuccessMessage}" Foreground="{StaticResource PrimaryBrush}" IsVisible="{Binding SuccessMessage, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>
            <StackPanel Orientation="Horizontal" Spacing="8">
              <TextBlock Text="{Binding [RefundTotal], Source={x:Static services:I18nService.Instance}}" FontSize="14" Foreground="{StaticResource Slate600Brush}" VerticalAlignment="Center"/>
              <TextBlock Text="{Binding TotalRefund, StringFormat='{}{0:N2}'}" FontSize="20" FontWeight="Bold" Foreground="{StaticResource Slate900Brush}"/>
            </StackPanel>
          </StackPanel>
          <Button Grid.Column="1" Classes="PrimaryButton" Command="{Binding SubmitReturnCommand}" IsEnabled="{Binding CanSubmit}" MinWidth="180" HorizontalContentAlignment="Center">
            <TextBlock Text="{Binding [ReturnAction], Source={x:Static services:I18nService.Instance}}"/>
          </Button>
        </Grid>
      </Border>
    </Grid>
  </Border>
</Window>
```

- [ ] **Step 2: Create the code-behind**

`src/VvCash/Views/ReturnsWindow.axaml.cs`:
```csharp
using Avalonia.Controls;

namespace VvCash.Views;

public partial class ReturnsWindow : Window
{
    public ReturnsWindow()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 3: Verify build (use isolated output if app running)**

Run: `dotnet build src/VvCash/VvCash.csproj -o build/verify`
Expected: Build succeeded (XAML compiles).

- [ ] **Step 4: Commit**

```bash
git add src/VvCash/Views/ReturnsWindow.axaml src/VvCash/Views/ReturnsWindow.axaml.cs
git commit -m "feat: add ReturnsWindow master-detail view"
```

---

## Task 10: Entry point (PosViewModel + PosView nav button)

**Files:**
- Modify: `src/VvCash/ViewModels/PosViewModel.cs` (fields ~23-36, ctor ~245-283, add command near `OpenParkedSales` ~807)
- Modify: `src/VvCash/Views/PosView.axaml` (nav StackPanel ~38)

- [ ] **Step 1: Add the service field**

In `src/VvCash/ViewModels/PosViewModel.cs`, after the line `private readonly IParkedSaleService _parkedSaleService;`:
```csharp
    private readonly IReturnService _returnService;
```

- [ ] **Step 2: Add the constructor parameter and assignment**

In the `PosViewModel(...)` constructor parameter list, after `IParkedSaleService parkedSaleService,`:
```csharp
        IReturnService returnService,
```
And in the body, after `_parkedSaleService = parkedSaleService;`:
```csharp
        _returnService = returnService;
```

- [ ] **Step 3: Add the `OpenReturns` command**

In `src/VvCash/ViewModels/PosViewModel.cs`, immediately after the `OpenParkedSales` method (ends ~824), add:
```csharp
    [RelayCommand]
    private async Task OpenReturns()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                var dialog = new VvCash.Views.ReturnsWindow();
                dialog.DataContext = new ReturnsViewModel(dialog, _returnService, _printerService, _settingsService);
                await dialog.ShowDialog(mainWindow);
            }
        }
    }
```

- [ ] **Step 4: Add the nav button**

In `src/VvCash/Views/PosView.axaml`, inside the `Grid.Column="2"` StackPanel, immediately after the closing `</Button>` of the Parked Sales button (line ~46), add:
```xml
                    <Button Classes="NavButton" Command="{Binding OpenReturnsCommand}" Margin="0,0,16,0" FontSize="14"
                            Content="{Binding [Returns], Source={x:Static services:I18nService.Instance}}"/>
```

- [ ] **Step 5: Verify build (use isolated output if app running)**

Run: `dotnet build src/VvCash/VvCash.csproj -o build/verify`
Expected: Build succeeded. (DI already supplies `IReturnService` to `PosViewModel` from Task 5.)

- [ ] **Step 6: Commit**

```bash
git add src/VvCash/ViewModels/PosViewModel.cs src/VvCash/Views/PosView.axaml
git commit -m "feat: open Returns window from POS nav bar"
```

---

## Task 11: Settings toggles in the Settings screen

**Files:**
- Read first: `src/VvCash/ViewModels/SettingsViewModel.cs`, `src/VvCash/Views/SettingsView.axaml`
- Modify: `src/VvCash/ViewModels/SettingsViewModel.cs`
- Modify: `src/VvCash/Views/SettingsView.axaml`

> This task wires the two flags from Task 2 to the UI. Match the existing SettingsViewModel pattern (it already loads/saves `ISettingsService` fields and binds to `SettingsView`). Read both files before editing.

- [ ] **Step 1: Add observable properties to `SettingsViewModel`**

Find where existing settings fields are declared as `[ObservableProperty]` (e.g. `SyncIntervalMinutes`, `BackendUrl`) and add alongside them:
```csharp
    [ObservableProperty] private bool _returnOpenCashDrawer = true;
    [ObservableProperty] private bool _returnPrintReceipt = true;
```

- [ ] **Step 2: Load from settings**

In the method/constructor that copies `ISettingsService` values into the VM (where `BackendUrl = _settingsService.BackendUrl;` etc. are set), add:
```csharp
        ReturnOpenCashDrawer = _settingsService.ReturnOpenCashDrawer;
        ReturnPrintReceipt = _settingsService.ReturnPrintReceipt;
```

- [ ] **Step 3: Persist on save**

In the Save command/method (where `_settingsService.BackendUrl = BackendUrl;` etc. are assigned before `_settingsService.Save();`), add:
```csharp
        _settingsService.ReturnOpenCashDrawer = ReturnOpenCashDrawer;
        _settingsService.ReturnPrintReceipt = ReturnPrintReceipt;
```

- [ ] **Step 4: Add the toggles to `SettingsView.axaml`**

In a sensible section (e.g. near Printers), add:
```xml
        <CheckBox IsChecked="{Binding ReturnOpenCashDrawer}"
                  Content="{Binding [ReturnOpenDrawerSetting], Source={x:Static services:I18nService.Instance}}"/>
        <CheckBox IsChecked="{Binding ReturnPrintReceipt}"
                  Content="{Binding [ReturnPrintReceiptSetting], Source={x:Static services:I18nService.Instance}}"/>
```
(If `SettingsView.axaml` lacks the `services` namespace alias, add `xmlns:services="clr-namespace:VvCash.Services"` to the root element — copy from `PosView.axaml`.)

- [ ] **Step 5: Verify build**

Run: `dotnet build src/VvCash/VvCash.csproj -o build/verify`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/VvCash/ViewModels/SettingsViewModel.cs src/VvCash/Views/SettingsView.axaml
git commit -m "feat: expose return post-action toggles in settings"
```

---

## Task 12: i18n keys

**Files:**
- Modify: `src/VvCash/Assets/i18n/en.json`
- Modify: `src/VvCash/Assets/i18n/ru.json`
- Modify: `src/VvCash/Assets/i18n/uz.json`
- Modify: `src/VvCash/Assets/i18n/tg.json`
- Modify: `src/VvCash/Assets/i18n/kk.json`

> Each file is a flat `{ "key": "value" }` map. Add the keys below before the final closing `}` (add a comma to the current last line). Use the per-locale values from the table.

Keys to add: `Returns`, `ReturnAction`, `Returnable`, `AlreadyReturned`, `SoldQty`, `RefundTotal`, `NoSales`, `ReturnSuccess`, `ReturnFailed`, `NoConnection`, `SelectSaleToReturn`, `Prev`, `Next`, `ReturnOpenDrawerSetting`, `ReturnPrintReceiptSetting`.

| key | en | ru | uz | tg | kk |
|---|---|---|---|---|---|
| Returns | Returns | Возвраты | Qaytarishlar | Бозгашт | Қайтарулар |
| ReturnAction | Return | Вернуть | Qaytarish | Бозгардондан | Қайтару |
| Returnable | Returnable | Доступно к возврату | Qaytarish mumkin | Имкони бозгашт | Қайтаруға болады |
| AlreadyReturned | Returned | Возвращено | Qaytarilgan | Баргардонида | Қайтарылған |
| SoldQty | Sold | Продано | Sotilgan | Фурӯхта | Сатылған |
| RefundTotal | Refund total | Сумма возврата | Qaytarish summasi | Маблағи бозгашт | Қайтару сомасы |
| NoSales | No sales | Нет продаж | Sotuvlar yo'q | Фурӯш нест | Сатылымдар жоқ |
| ReturnSuccess | Return completed | Возврат выполнен | Qaytarish bajarildi | Бозгашт анҷом шуд | Қайтару орындалды |
| ReturnFailed | Return failed | Ошибка возврата | Qaytarishda xatolik | Хатои бозгашт | Қайтару қатесі |
| NoConnection | No connection | Нет соединения | Aloqa yo'q | Пайваст нест | Байланыс жоқ |
| SelectSaleToReturn | Select a sale to return items | Выберите продажу для возврата | Qaytarish uchun sotuvni tanlang | Барои бозгашт фурӯшро интихоб кунед | Қайтару үшін сатылымды таңдаңыз |
| Prev | Prev | Назад | Oldingi | Қаблӣ | Алдыңғы |
| Next | Next | Вперёд | Keyingi | Баъдӣ | Келесі |
| ReturnOpenDrawerSetting | Open cash drawer on return | Открывать денежный ящик при возврате | Qaytarishda kassa qutisini ochish | Кушодани қуттии пул ҳангоми бозгашт | Қайтару кезінде кассаны ашу |
| ReturnPrintReceiptSetting | Print return receipt | Печатать чек возврата | Qaytarish chekini chop etish | Чопи чеки бозгашт | Қайтару чегін басу |

- [ ] **Step 1: Add the keys to all five files** (values from the table).

- [ ] **Step 2: Validate JSON**

Run: `dotnet build src/VvCash/VvCash.csproj -o build/verify`
Expected: Build succeeded (Avalonia copies Assets; malformed JSON would still build, so also eyeball each file or run a JSON linter).

- [ ] **Step 3: Commit**

```bash
git add src/VvCash/Assets/i18n/en.json src/VvCash/Assets/i18n/ru.json src/VvCash/Assets/i18n/uz.json src/VvCash/Assets/i18n/tg.json src/VvCash/Assets/i18n/kk.json
git commit -m "feat: add i18n keys for returns"
```

---

## Task 13: Full-suite run + manual verification

**Files:** none (verification only)

- [ ] **Step 1: Run the whole test suite (app closed)**

Run: `dotnet test tests/VvCash.Tests/VvCash.Tests.csproj`
Expected: PASS — all tests (models, service, settings, printer, row VM, ViewModel).

- [ ] **Step 2: Launch the app**

Run the app the normal way. Open a shift, then click the **Returns** nav button.

- [ ] **Step 3: Manual return**

1. Confirm the sales list loads (paged).
2. Select a sale → returnable lines load; fully-returned lines are disabled.
3. Set a return quantity on one line (cannot exceed `Sold − Returned`); confirm refund total updates.
4. Click **Return** → success message; line's "Returned" increases on reload.
5. Toggle the two settings off in Settings, repeat: confirm no drawer kick / no receipt print.

- [ ] **Step 4: Final commit (if any fixups)**

```bash
git add -A
git commit -m "chore: product returns verification fixups"
```

---

## Self-Review Notes

- **Spec coverage:** models (T3), service routed via AuthHeaderHandler (T4+T5), per-line clamp (T7), master-detail window (T9), original `selected_date` as date-only (T8 `BuildRequest`), online-only error surfacing (T8), configurable drawer+print (T2/T6/T8/T11), entry point in nav (T10), i18n (T12), tests + manual (T1–T13). All spec sections mapped.
- **Deviation from spec:** the printer prints from a decoupled `ReturnReceiptLine` record (Task 6) instead of `ReturnLineVm` directly — keeps the hardware layer independent of the ViewModel. Functionally identical.
- **Assumption:** refund per line = `ReturnQty × after_discount` (treating `after_discount` as the per-unit post-discount price, consistent with the live sample where `after_discount == sold_price == 100` for `quantity 1`). If the backend treats `after_discount` as a line total, adjust `ReturnLineVm.LineRefund` to not multiply — isolated to one property.
- **Type consistency:** `IReturnService` signatures, `ReturnLineVm` members, and printer method names match across all tasks.
