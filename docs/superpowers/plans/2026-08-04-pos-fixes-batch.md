# POS Fixes Batch (client card, return/exchange receipt, scanner, layout) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix seven cashier-reported issues in the client card, the return/exchange receipt, and the return/exchange screens: (1) client full name not showing right after save, (2) receipt missing the sale's warehouse, (3) receipt missing a formatted date, (4) receipt missing the seller, (5) no barcode scanning on the "item brought back" side, (6) no Enter/scanner support on the "item issued" side, (7) long product names overlapping the qty +/− buttons, plus making phone the one required field on the client card.

**Architecture:** Two repos. `C:\work\cloudmarket-server` (Go backend, one Postgres DB per tenant) gets one additive column (`warehouse_name`) on the existing `GET /documents/expense/` response — no schema change, no migration, just a wider SELECT. `C:\work\vv-cash` (Avalonia/C# desktop register) gets the rest: a computed fallback on a response model, a stricter client-card validation gate, new optional parameters threaded through the existing ESC/POS receipt builders, two new barcode-driven commands on the return/exchange view models, and two small XAML fixes.

**Tech Stack:** C# / .NET, Avalonia UI (reflective bindings — `AvaloniaUseCompiledBindingsByDefault=false`, so a typo'd binding path compiles and fails silently, verify by running the app), CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`), xUnit. Backend: Go, pgx, Gin.

**Correspondence to the original request:**
1. Client full name not shown right after save → Task 1
2. Show the sale's warehouse/shop on the return/exchange receipt → Task 3 (backend) + Task 4
3. Format the receipt's date/time → Task 4 (also fixes the same raw-ISO string on screen)
4. Show the seller who made the original sale → Task 4
5. Scan the item being returned → Task 5; scanner Enter-support on the issued side → Task 6
6. Make every client-card field optional except phone → Task 2 (phone becomes the one *required* field — see the clarified answer below)
7. Don't unify the "issued" card with the "returned" card (already true — they're already three separate templates); fix the long-name-overlaps-the-buttons bug → Task 7

---

## Task 1: Client's full name shows immediately after registration

**Files:**
- Modify: `src/VvCash/Models/Api/CounterpartyResponse.cs`
- Modify: `tests/VvCash.Tests/CustomerRegistrationViewModelTest.cs:28`
- Modify: `tests/VvCash.Tests/CustomerSearchViewModelTest.cs:55`

**Root cause:** `CounterpartyResponse.FullName` is a plain settable string deserialized straight from the server's `full_name` field. If the create-endpoint's response ever omits or blanks it (unlike the search endpoint, which always fills it), the freshly created client's name is blank in the status line (`PosViewModel.cs:1591`/`1593`), the customer chip (`PosViewModel.cs:261`), and the search list (`CustomerSearchWindow.axaml:78`) — even though the cashier just typed a valid first/last name that the request carried correctly. `SellerInfo.cs:34` already solves the identical problem with a computed `FullName => $"{FirstName} {LastName}".Trim()`; `CounterpartyResponse` needs the same fallback.

- [ ] **Step 1: Rename the raw field and add the computed fallback**

In `src/VvCash/Models/Api/CounterpartyResponse.cs`, replace:

```csharp
    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }
```

with:

```csharp
    [JsonPropertyName("full_name")]
    public string? FullNameRaw { get; set; }

    /// <summary>Falls back to the names the cashier typed when the server's own
    /// full_name comes back blank — seen on the create-counterparty response even
    /// when FirstName/LastName both came back filled. Without the fallback a
    /// freshly created client's name showed up empty in the status line, the
    /// customer chip, and the search list, right after the cashier had typed it
    /// correctly. Mirrors SellerInfo.FullName.</summary>
    [JsonIgnore]
    public string FullName => !string.IsNullOrWhiteSpace(FullNameRaw)
        ? FullNameRaw!
        : $"{FirstName} {LastName}".Trim();
```

- [ ] **Step 2: Update the two tests that construct `CounterpartyResponse` with `FullName =`**

`FullName` is now computed and read-only, so both places that set it directly must set `FullNameRaw` instead — behavior is unchanged since both already pass a non-empty string.

In `tests/VvCash.Tests/CustomerRegistrationViewModelTest.cs:28`, change:

```csharp
        public CounterpartyResponse? CreateResult = new() { Id = "c-1", FullName = "Новый Клиент" };
```

to:

```csharp
        public CounterpartyResponse? CreateResult = new() { Id = "c-1", FullNameRaw = "Новый Клиент" };
```

In `tests/VvCash.Tests/CustomerSearchViewModelTest.cs:55`, change:

```csharp
    private static CounterpartyResponse Customer(string id, string name)
        => new() { Id = id, FullName = name };
```

to:

```csharp
    private static CounterpartyResponse Customer(string id, string name)
        => new() { Id = id, FullNameRaw = name };
```

- [ ] **Step 3: Add a regression test for the fallback**

Add to `tests/VvCash.Tests/` a new file `CounterpartyResponseTest.cs`:

```csharp
using VvCash.Models.Api;
using Xunit;

namespace VvCash.Tests;

public class CounterpartyResponseTest
{
    [Fact]
    public void FullName_FallsBackToFirstAndLastName_WhenServerOmitsIt()
    {
        var response = new CounterpartyResponse { FirstName = "Иван", LastName = "Петров" };

        Assert.Equal("Иван Петров", response.FullName);
    }

    [Fact]
    public void FullName_PrefersTheServersOwnValue_WhenPresent()
    {
        var response = new CounterpartyResponse
        {
            FirstName = "Иван", LastName = "Петров", FullNameRaw = "Петров Иван Иванович",
        };

        Assert.Equal("Петров Иван Иванович", response.FullName);
    }
}
```

- [ ] **Step 4: Build and run the affected tests**

Run: `dotnet test tests/VvCash.Tests --filter "FullyQualifiedName~CounterpartyResponseTest|FullyQualifiedName~CustomerRegistrationViewModelTest|FullyQualifiedName~CustomerSearchViewModelTest|FullyQualifiedName~CounterpartyServiceTest"`
Expected: all pass (the app is likely running — see the build-lock note below; build to `-o build/verify` if `dotnet build`/`dotnet test`'s own build step fails on a file lock).

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Models/Api/CounterpartyResponse.cs tests/VvCash.Tests/CustomerRegistrationViewModelTest.cs tests/VvCash.Tests/CustomerSearchViewModelTest.cs tests/VvCash.Tests/CounterpartyResponseTest.cs
git commit -m "fix(customer): show the typed name immediately when the server omits full_name"
```

---

## Task 2: Phone becomes the one required field on the client card

**Files:**
- Modify: `src/VvCash/ViewModels/CustomerRegistrationViewModel.cs:96-109`
- Modify: `tests/VvCash.Tests/CustomerRegistrationViewModelTest.cs:197-210`
- Modify: `src/VvCash/Assets/i18n/{ru,en,kk,tg,uz}.json`

**Current state:** every field is already optional (first/last name default to `"-"`, email/DOB/gender are never validated) — nothing needs to change there. Phone is the one field that needs to flip from optional to required: today `SubmitAsync` only rejects a *partially* typed phone; an empty one submits fine. Confirmed with the user: block Save entirely when the phone box is empty.

- [ ] **Step 1: Add the `PhoneRequired` key to every locale file**

In `src/VvCash/Assets/i18n/ru.json`, insert before the final `}` (after `"UpdateLaunchFailed": ...`):

```json
  "UpdateLaunchFailed": "Не удалось запустить установку. Запустите файл вручную:",
  "PhoneRequired": "Укажите номер телефона"
}
```

In `src/VvCash/Assets/i18n/en.json`:

```json
  "UpdateLaunchFailed": "Could not start the installer. Run this file by hand:",
  "PhoneRequired": "Enter a phone number"
}
```

In `src/VvCash/Assets/i18n/kk.json`:

```json
  "UpdateLaunchFailed": "Орнатуды бастау мүмкін болмады. Файлды қолмен іске қосыңыз:",
  "PhoneRequired": "Телефон нөмірін көрсетіңіз"
}
```

In `src/VvCash/Assets/i18n/tg.json`:

```json
  "UpdateLaunchFailed": "Насбкуниро оғоз карда нашуд. Файлро дастӣ оғоз кунед:",
  "PhoneRequired": "Рақами телефонро нишон диҳед"
}
```

In `src/VvCash/Assets/i18n/uz.json`:

```json
  "UpdateLaunchFailed": "O'rnatishni boshlab bo'lmadi. Faylni qo'lda ishga tushiring:",
  "PhoneRequired": "Telefon raqamini kiriting"
}
```

- [ ] **Step 2: Write the failing test**

Replace the existing `EmptyPhone_SubmitsWithoutOne` test in `tests/VvCash.Tests/CustomerRegistrationViewModelTest.cs:197-210` (it asserts the old, now-wrong behavior) with:

```csharp
    /// <summary>Телефон — единственное обязательное поле карточки клиента: без
    /// него сохранять больше нельзя, даже если остальные поля заполнены.</summary>
    [Fact]
    public async Task EmptyPhone_IsRefusedAndNeverSent()
    {
        var harness = new Harness();
        var vm = harness.Build("TJ");
        vm.FirstName = "Иван";

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(0, harness.Service.CreateCount);
        Assert.Equal(0, harness.CloseCount);
    }
```

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test tests/VvCash.Tests --filter "FullyQualifiedName~EmptyPhone_IsRefusedAndNeverSent"`
Expected: FAIL — `Assert.NotNull(vm.ErrorMessage)` fails because today an empty phone submits successfully.

- [ ] **Step 4: Make phone required**

In `src/VvCash/ViewModels/CustomerRegistrationViewModel.cs:96-109`, replace:

```csharp
    [RelayCommand]
    private async Task SubmitAsync()
    {
        ErrorMessage = null;

        // Пустой телефон законен — клиент без телефона нормальная запись. А вот
        // начатый и не дописанный раньше молча превращался в Phone = null:
        // кассир набирал восемь цифр из девяти, жал «Сохранить» и получал
        // клиента без телефона, ничего об этом не узнав.
        if (PhoneNumber.Length > 0 && PhoneNumber.Length != _phoneFormat.DigitCount)
        {
            ErrorMessage = I18nService.Instance["PhoneIncomplete"];
            return;
        }
```

with:

```csharp
    [RelayCommand]
    private async Task SubmitAsync()
    {
        ErrorMessage = null;

        // Телефон — единственное обязательное поле карточки клиента: без него
        // потом нельзя ни найти клиента на кассе, ни связаться с ним. Остальные
        // поля (имя, фамилия, email, дата рождения, пол) как были необязательными,
        // так и остаются.
        if (PhoneNumber.Length == 0)
        {
            ErrorMessage = I18nService.Instance["PhoneRequired"];
            return;
        }

        // Начатый и не дописанный номер раньше молча превращался в Phone = null:
        // кассир набирал восемь цифр из девяти, жал «Сохранить» и получал
        // клиента без телефона, ничего об этом не узнав.
        if (PhoneNumber.Length != _phoneFormat.DigitCount)
        {
            ErrorMessage = I18nService.Instance["PhoneIncomplete"];
            return;
        }
```

- [ ] **Step 5: Run the test to verify it passes, then the full file**

Run: `dotnet test tests/VvCash.Tests --filter "FullyQualifiedName~CustomerRegistrationViewModelTest"`
Expected: PASS — all tests in the file, including the new `EmptyPhone_IsRefusedAndNeverSent`.

- [ ] **Step 6: Commit**

```bash
git add src/VvCash/ViewModels/CustomerRegistrationViewModel.cs tests/VvCash.Tests/CustomerRegistrationViewModelTest.cs src/VvCash/Assets/i18n/ru.json src/VvCash/Assets/i18n/en.json src/VvCash/Assets/i18n/kk.json src/VvCash/Assets/i18n/tg.json src/VvCash/Assets/i18n/uz.json
git commit -m "fix(customer): require a phone number to save a client card"
```

---

## Task 3 (backend, `C:\work\cloudmarket-server`): expose the sale's warehouse on `GET /documents/expense/`

**Files (all in `C:\work\cloudmarket-server`):**
- Modify: `documents/expense_serializers.go:11-24`
- Modify: `documents/document_expense_repo.go:54-94`

**Why this is safe and small:** `document_expenses.warehouse_id` and the `warehouses` table (with a `name` column) have existed since 2018 — no migration needed, only a wider SELECT. The pattern (`document_expenses.warehouse_id → warehouses.id → warehouses.name`) is already used elsewhere in this codebase (`GetExpenseWarehouseID`, `documents/document_return_repo.go:191-198`). A tenant DB can have multiple warehouses (confirmed: `warehouses.store_id`, `cashes.warehouse_id`, existing `ChangePriceFilterBuilder` filtering by `r.warehouse_id`), so this is a real per-document value, not a DB-wide constant.

- [ ] **Step 1: Add `WarehouseName` to the response struct**

In `documents/expense_serializers.go`, replace:

```go
type expenseListSerializer struct {
	SelectedDate   time.Time       `json:"selected_date"`
	CreatedAt      time.Time       `json:"created_at"`
	ID             string          `json:"id"`
	State          string          `json:"state"`
	Creator        string          `json:"creator"`
	Counterparty   string          `json:"counterparty"`
	DocumentNumber string          `json:"document_number"`
	Cost           decimal.Decimal `json:"cost"`
	ToPay          decimal.Decimal `json:"to_pay"`
	Discount       float64         `json:"discount"`
	Payed          decimal.Decimal `json:"payed"`
	Remain         decimal.Decimal `json:"remain"`
}
```

with:

```go
type expenseListSerializer struct {
	SelectedDate   time.Time       `json:"selected_date"`
	CreatedAt      time.Time       `json:"created_at"`
	ID             string          `json:"id"`
	State          string          `json:"state"`
	Creator        string          `json:"creator"`
	Counterparty   string          `json:"counterparty"`
	DocumentNumber string          `json:"document_number"`
	Cost           decimal.Decimal `json:"cost"`
	ToPay          decimal.Decimal `json:"to_pay"`
	Discount       float64         `json:"discount"`
	Payed          decimal.Decimal `json:"payed"`
	Remain         decimal.Decimal `json:"remain"`
	// The warehouse the sale itself was rung up from — not necessarily this
	// register's own warehouse, since a return/exchange can be looked up from
	// any register in the tenant. Empty when the document has no linked
	// document_expenses row (LEFT JOIN) rather than null, so callers never have
	// to nil-check it.
	WarehouseName string `json:"warehouse_name"`
}
```

- [ ] **Step 2: Select and scan the warehouse name**

In `documents/document_expense_repo.go:54-94`, replace the `ExpenseList` function body's query and scan with:

```go
	q := `SELECT
		d.id, d.document_number, d."cost", d.to_pay, d.discount, d.payed, d.remain,
		d.selected_date, d.created_at, s."name" state,
		concat(u.first_name, ' ', u.last_name) as creator,
		concat(c.first_name, ' ', c.last_name) as counterparty,
		COALESCE(w."name", '') as warehouse_name
	FROM (
		SELECT DISTINCT ON (d.id) d.id, d.document_number, d."cost", d.to_pay, d.discount,
			d.payed, d.remain, d.selected_date, d.created_at, d.state_id, d.created_by_id, d.counterparty_id,
			de.warehouse_id
		FROM document_bases d
		LEFT JOIN document_expenses de ON de.document_base_id = d.id
		LEFT JOIN document_expense_details ded ON ded.document_expense_id = de.id
		` + fb.WhereClause() + `
		ORDER BY d.id, d.selected_date DESC
	) d
	LEFT JOIN states s ON s.id = d.state_id
	LEFT JOIN users u ON u.id = d.created_by_id
	LEFT JOIN counterparties c ON c.id = d.counterparty_id
	LEFT JOIN warehouses w ON w.id = d.warehouse_id
	ORDER BY d.selected_date DESC
	LIMIT ` + limitParam + ` OFFSET ` + offsetParam

	rows, err := db.Query(ctx, q, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var result []expenseListSerializer
	for rows.Next() {
		var item expenseListSerializer
		if err := rows.Scan(
			&item.ID, &item.DocumentNumber, &item.Cost, &item.ToPay, &item.Discount,
			&item.Payed, &item.Remain, &item.SelectedDate, &item.CreatedAt,
			&item.State, &item.Creator, &item.Counterparty, &item.WarehouseName,
		); err != nil {
			return nil, err
		}
		result = append(result, item)
	}

	return result, rows.Err()
}
```

(Only the `q :=` query literal and the `rows.Scan(...)` argument list change; the surrounding function signature, `fb`/`args` setup above, and everything after this function are untouched.)

- [ ] **Step 3: Build**

Run: `go build ./...` (from `C:\work\cloudmarket-server`)
Expected: no errors.

- [ ] **Step 4: Verify against the local test DB**

Per this repo's own convention, DB-gated Go tests need a local Postgres on `:5433` to actually run (they skip silently otherwise) — no existing test exercises `ExpenseList`'s SQL directly, and standing up a fixture for it is out of scope here. Verify by hand instead:

Run: `go run ./cmd/server &` (or however this repo's dev server is normally started), then:

```bash
curl -s "http://localhost:PORT/documents/expense/" -H "Authorization: Bearer <a valid token>" | head -c 500
```

Expected: each item in the JSON body's `body` array now has a `"warehouse_name"` field with a non-empty value for any document that has a linked `document_expenses` row with a `warehouse_id`.

- [ ] **Step 5: Commit**

```bash
git add documents/expense_serializers.go documents/document_expense_repo.go
git commit -m "feat(documents): expose the sale's warehouse on GET /documents/expense/"
```

**Deploy note (not a plan step, just so it isn't missed):** this needs the Go service rebuilt/redeployed per tenant same as any other code change — no migration to run, since `warehouse_id`/`warehouses` already exist in every tenant's schema.

---

## Task 4 (`C:\work\vv-cash`): print warehouse, formatted date, and seller on the return/exchange receipt

**Depends on Task 3 being deployed for `WarehouseName` to actually have a value at runtime — the code here compiles and works with it empty either way.**

**Files:**
- Modify: `src/VvCash/Models/Api/ReturnModels.cs` (`ExpenseListItem`)
- Modify: `src/VvCash/Services/Hardware/IPrinterService.cs`
- Modify: `src/VvCash/Services/Hardware/EscPosPrinterService.cs`
- Modify: `src/VvCash/Services/Hardware/CompositePrinterService.cs`
- Modify: `src/VvCash/Services/Hardware/MockPrinterService.cs`
- Modify: `src/VvCash/ViewModels/ReturnsViewModel.cs:224-242` (`RunPostReturnActionsAsync`)
- Modify: `src/VvCash/ViewModels/ExchangeViewModel.cs:878-897` (`RunPostExchangeActionsAsync`)
- Modify: `src/VvCash/Views/ExchangeWindow.axaml:71`
- Modify: `src/VvCash/Views/ReturnsWindow.axaml:70`
- Modify: `tests/VvCash.Tests/EscPosReturnTest.cs`
- Modify: `tests/VvCash.Tests/EscPosExchangeTest.cs`
- Modify: `tests/VvCash.Tests/ReturnsViewModelTest.cs:52-62` (`CountingPrinter` fake)
- Modify: `tests/VvCash.Tests/ExchangeViewModelTest.cs:138-159` (`CountingPrinter` fake)

**Design:** three new *optional* trailing parameters (`warehouseName`, `sellerName`, `saleDate`) on both `PrintReturnReceiptAsync`/`BuildReturnReceipt` and `PrintExchangeReceiptAsync`/`BuildExchangeReceipt`, so every existing call site and test keeps compiling unchanged unless it wants to pass the new values. The two view models already have the data in scope (`SelectedSale.WarehouseName`, `SelectedSale.Creator`) or one property away (`SelectedSale.FormattedSelectedDate`, added here). The receipt's static labels stay in the file's existing plain-ASCII style (`"Doc #..."`, `"RETURN / VOZVRAT"`); only the *values* (a warehouse's or seller's actual name) carry Cyrillic, same as the line items already do.

- [ ] **Step 1: Add `WarehouseName` and a formatted date to `ExpenseListItem`**

In `src/VvCash/Models/Api/ReturnModels.cs`, change the top of the file from:

```csharp
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VvCash.Models.Api;
```

to:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace VvCash.Models.Api;
```

Then replace the `ExpenseListItem` class:

```csharp
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
```

with:

```csharp
public class ExpenseListItem
{
    [JsonPropertyName("selected_date")] public string? SelectedDate { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("creator")] public string? Creator { get; set; }
    [JsonPropertyName("counterparty")] public string? Counterparty { get; set; }
    [JsonPropertyName("warehouse_name")] public string? WarehouseName { get; set; }
    [JsonPropertyName("document_number")] public string? DocumentNumber { get; set; }
    [JsonPropertyName("cost")] public decimal Cost { get; set; }
    [JsonPropertyName("to_pay")] public decimal ToPay { get; set; }
    [JsonPropertyName("discount")] public decimal Discount { get; set; }
    [JsonPropertyName("payed")] public decimal Payed { get; set; }
    [JsonPropertyName("remain")] public decimal Remain { get; set; }

    /// <summary>SelectedDate formatted for a cashier to read. The API sends UTC
    /// ISO-8601 (e.g. "2026-06-06T17:32:55.052Z"); both the sale-picker card and
    /// the printed return/exchange receipt were showing that raw string verbatim.
    /// Falls back to the raw string when it doesn't parse, rather than showing
    /// nothing.</summary>
    [JsonIgnore]
    public string FormattedSelectedDate
        => DateTimeOffset.TryParse(SelectedDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
            : SelectedDate ?? string.Empty;
}
```

- [ ] **Step 2: Write the failing receipt-builder tests**

Add to `tests/VvCash.Tests/EscPosReturnTest.cs`, inside the `EscPosReturnTest` class:

```csharp
    [Fact]
    public void ReturnReceiptBuffer_IncludesWarehouseSellerAndDate_WhenGiven()
    {
        var lines = new List<ReturnReceiptLine> { new("Salad", 2, 200m) };
        var bytes = EscPosPrinterService.BuildReturnReceipt(
            lines, 200m, "9", warehouseName: "Central Store", sellerName: "Ivanov I.", saleDate: "06.06.2026 17:32");
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("Central Store", text);
        Assert.Contains("Ivanov I.", text);
        Assert.Contains("06.06.2026 17:32", text);
    }

    [Fact]
    public void ReturnReceiptBuffer_OmitsTheNewLines_WhenNotGiven()
    {
        var lines = new List<ReturnReceiptLine> { new("Salad", 2, 200m) };
        var bytes = EscPosPrinterService.BuildReturnReceipt(lines, 200m, "9");
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("Whse:", text);
        Assert.DoesNotContain("Seller:", text);
    }
```

Add to `tests/VvCash.Tests/EscPosExchangeTest.cs`, inside the `EscPosExchangeTest` class:

```csharp
    [Fact]
    public void ExchangeReceiptBuffer_IncludesWarehouseSellerAndDate_WhenGiven()
    {
        var returned = new List<ReturnReceiptLine> { new("Old Shirt", 1, 80m) };
        var issued = new List<ReturnReceiptLine> { new("New Shirt", 1, 130m) };

        var bytes = EscPosPrinterService.BuildExchangeReceipt(
            returned, issued, 50m, "9", warehouseName: "Central Store", sellerName: "Ivanov I.", saleDate: "06.06.2026 17:32");
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("Central Store", text);
        Assert.Contains("Ivanov I.", text);
        Assert.Contains("06.06.2026 17:32", text);
    }
```

- [ ] **Step 3: Run the new tests to verify they fail**

Run: `dotnet test tests/VvCash.Tests --filter "FullyQualifiedName~EscPosReturnTest|FullyQualifiedName~EscPosExchangeTest"`
Expected: FAIL to compile — `BuildReturnReceipt`/`BuildExchangeReceipt` don't accept `warehouseName`/`sellerName`/`saleDate` yet.

- [ ] **Step 4: Add the new parameters to the interface**

In `src/VvCash/Services/Hardware/IPrinterService.cs`, replace:

```csharp
    System.Threading.Tasks.Task<bool> PrintReturnReceiptAsync(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> lines,
        decimal totalRefund, string documentNumber);

    /// <param name="difference">Positive: the customer owes the difference.
    /// Negative: the till refunds it. Only its absolute value is printed — the
    /// label carries the sign.</param>
    System.Threading.Tasks.Task<bool> PrintExchangeReceiptAsync(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber);
```

with:

```csharp
    /// <param name="warehouseName">The warehouse/store the original sale was rung
    /// up from. Null or empty prints no such line.</param>
    /// <param name="sellerName">Who made the original sale. Null or empty prints
    /// no such line.</param>
    /// <param name="saleDate">Already formatted for display — this layer prints it
    /// verbatim rather than parsing it itself. Null or empty prints no such line.</param>
    System.Threading.Tasks.Task<bool> PrintReturnReceiptAsync(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> lines,
        decimal totalRefund, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null);

    /// <param name="difference">Positive: the customer owes the difference.
    /// Negative: the till refunds it. Only its absolute value is printed — the
    /// label carries the sign.</param>
    /// <param name="warehouseName">The warehouse/store the original sale was rung
    /// up from. Null or empty prints no such line.</param>
    /// <param name="sellerName">Who made the original sale. Null or empty prints
    /// no such line.</param>
    /// <param name="saleDate">Already formatted for display. Null or empty prints
    /// no such line.</param>
    System.Threading.Tasks.Task<bool> PrintExchangeReceiptAsync(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null);
```

- [ ] **Step 5: Print the new lines in the real ESC/POS builders**

In `src/VvCash/Services/Hardware/EscPosPrinterService.cs`, replace `BuildReturnReceipt` and `PrintReturnReceiptAsync`:

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
```

with:

```csharp
    public static byte[] BuildReturnReceipt(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> lines,
        decimal totalRefund, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        using var ms = new MemoryStream();
        Write(ms, CmdInit);
        Write(ms, CmdAlignCenter);
        Write(ms, CmdDoubleSizeOn);
        WriteLine(ms, "RETURN / VOZVRAT");
        Write(ms, CmdDoubleSizeOff);
        WriteLine(ms, $"Doc #{documentNumber}");
        if (!string.IsNullOrWhiteSpace(saleDate)) WriteLine(ms, saleDate);
        if (!string.IsNullOrWhiteSpace(warehouseName)) WriteLine(ms, $"Whse: {warehouseName}");
        if (!string.IsNullOrWhiteSpace(sellerName)) WriteLine(ms, $"Seller: {sellerName}");
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
        decimal totalRefund, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        try
        {
            await SendAsync(BuildReturnReceipt(lines, totalRefund, documentNumber, warehouseName, sellerName, saleDate));
            return true;
        }
        catch
        {
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }
```

In the same file, replace `BuildExchangeReceipt` and `PrintExchangeReceiptAsync`:

```csharp
    public static byte[] BuildExchangeReceipt(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber)
    {
        using var ms = new MemoryStream();
        Write(ms, CmdInit);
        Write(ms, CmdAlignCenter);
        Write(ms, CmdDoubleSizeOn);
        WriteLine(ms, "EXCHANGE / OBMEN");
        Write(ms, CmdDoubleSizeOff);
        WriteLine(ms, $"Doc #{documentNumber}");
        WriteLine(ms, "----------------------------");
        Write(ms, CmdAlignLeft);

        WriteLine(ms, "RETURNED:");
        foreach (var l in returned)
            WriteLine(ms, PadLine($"{l.Name} x{l.Quantity}", $"{l.LineRefund:F2}", 32));

        WriteLine(ms, "ISSUED:");
        foreach (var l in issued)
            WriteLine(ms, PadLine($"{l.Name} x{l.Quantity}", $"{l.LineRefund:F2}", 32));

        WriteLine(ms, "----------------------------");
        Write(ms, CmdBoldOn);
        // An even swap owes nothing in either direction; without its own label it
        // printed "REFUND: 0.00" and invited the customer to ask for the money.
        var label = difference > 0 ? "AMOUNT DUE:" : difference < 0 ? "REFUND:" : "NO DIFFERENCE:";
        WriteLine(ms, PadLine(label, $"{Math.Abs(difference):F2}", 32));
        Write(ms, CmdBoldOff);
        Write(ms, CmdLineFeed);
        Write(ms, CmdLineFeed);
        Write(ms, CmdCut);
        return ms.ToArray();
    }

    public async Task<bool> PrintExchangeReceiptAsync(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber)
    {
        try
        {
            await SendAsync(BuildExchangeReceipt(returned, issued, difference, documentNumber));
            return true;
        }
        catch
        {
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }
```

with:

```csharp
    public static byte[] BuildExchangeReceipt(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        using var ms = new MemoryStream();
        Write(ms, CmdInit);
        Write(ms, CmdAlignCenter);
        Write(ms, CmdDoubleSizeOn);
        WriteLine(ms, "EXCHANGE / OBMEN");
        Write(ms, CmdDoubleSizeOff);
        WriteLine(ms, $"Doc #{documentNumber}");
        if (!string.IsNullOrWhiteSpace(saleDate)) WriteLine(ms, saleDate);
        if (!string.IsNullOrWhiteSpace(warehouseName)) WriteLine(ms, $"Whse: {warehouseName}");
        if (!string.IsNullOrWhiteSpace(sellerName)) WriteLine(ms, $"Seller: {sellerName}");
        WriteLine(ms, "----------------------------");
        Write(ms, CmdAlignLeft);

        WriteLine(ms, "RETURNED:");
        foreach (var l in returned)
            WriteLine(ms, PadLine($"{l.Name} x{l.Quantity}", $"{l.LineRefund:F2}", 32));

        WriteLine(ms, "ISSUED:");
        foreach (var l in issued)
            WriteLine(ms, PadLine($"{l.Name} x{l.Quantity}", $"{l.LineRefund:F2}", 32));

        WriteLine(ms, "----------------------------");
        Write(ms, CmdBoldOn);
        // An even swap owes nothing in either direction; without its own label it
        // printed "REFUND: 0.00" and invited the customer to ask for the money.
        var label = difference > 0 ? "AMOUNT DUE:" : difference < 0 ? "REFUND:" : "NO DIFFERENCE:";
        WriteLine(ms, PadLine(label, $"{Math.Abs(difference):F2}", 32));
        Write(ms, CmdBoldOff);
        Write(ms, CmdLineFeed);
        Write(ms, CmdLineFeed);
        Write(ms, CmdCut);
        return ms.ToArray();
    }

    public async Task<bool> PrintExchangeReceiptAsync(
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        try
        {
            await SendAsync(BuildExchangeReceipt(returned, issued, difference, documentNumber, warehouseName, sellerName, saleDate));
            return true;
        }
        catch
        {
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }
```

- [ ] **Step 6: Run the receipt-builder tests to verify they pass**

Run: `dotnet test tests/VvCash.Tests --filter "FullyQualifiedName~EscPosReturnTest|FullyQualifiedName~EscPosExchangeTest"`
Expected: still fails to build — `CompositePrinterService`/`MockPrinterService`/the two test fakes don't implement the new interface parameters yet. Continue to the next steps first, then re-run.

- [ ] **Step 7: Update the pass-through composite printer**

In `src/VvCash/Services/Hardware/CompositePrinterService.cs`, replace:

```csharp
    public async Task<bool> PrintReturnReceiptAsync(
        IEnumerable<VvCash.Models.ReturnReceiptLine> lines, decimal totalRefund, string documentNumber)
    {
        if (!_printers.Any()) return false;
        var list = lines.ToList();
        var tasks = _printers.Select(p => p.PrintReturnReceiptAsync(list, totalRefund, documentNumber)).ToList();
        await Task.WhenAll(tasks);
        return tasks.Any(t => t.Result);
    }

    public async Task<bool> PrintExchangeReceiptAsync(
        IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber)
    {
        if (!_printers.Any()) return false;
        var returnedList = returned.ToList();
        var issuedList = issued.ToList();
        var tasks = _printers.Select(p => p.PrintExchangeReceiptAsync(returnedList, issuedList, difference, documentNumber)).ToList();
        await Task.WhenAll(tasks);
        return tasks.Any(t => t.Result);
    }
```

with:

```csharp
    public async Task<bool> PrintReturnReceiptAsync(
        IEnumerable<VvCash.Models.ReturnReceiptLine> lines, decimal totalRefund, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        if (!_printers.Any()) return false;
        var list = lines.ToList();
        var tasks = _printers.Select(p => p.PrintReturnReceiptAsync(list, totalRefund, documentNumber, warehouseName, sellerName, saleDate)).ToList();
        await Task.WhenAll(tasks);
        return tasks.Any(t => t.Result);
    }

    public async Task<bool> PrintExchangeReceiptAsync(
        IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        if (!_printers.Any()) return false;
        var returnedList = returned.ToList();
        var issuedList = issued.ToList();
        var tasks = _printers.Select(p => p.PrintExchangeReceiptAsync(returnedList, issuedList, difference, documentNumber, warehouseName, sellerName, saleDate)).ToList();
        await Task.WhenAll(tasks);
        return tasks.Any(t => t.Result);
    }
```

- [ ] **Step 8: Update the mock printer**

In `src/VvCash/Services/Hardware/MockPrinterService.cs`, replace:

```csharp
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

    public Task<bool> PrintExchangeReceiptAsync(
        IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber)
    {
        Console.WriteLine($"=== EXCHANGE #{documentNumber} ===");
        Console.WriteLine("RETURNED:");
        foreach (var l in returned)
            Console.WriteLine($"  {l.Name} x{l.Quantity}  {l.LineRefund:F2}");
        Console.WriteLine("ISSUED:");
        foreach (var l in issued)
            Console.WriteLine($"  {l.Name} x{l.Quantity}  {l.LineRefund:F2}");
        Console.WriteLine(difference > 0 ? $"AMOUNT DUE: {difference:F2}" : $"REFUND: {-difference:F2}");
        Console.WriteLine("===============");
        return Task.FromResult(true);
    }
```

with:

```csharp
    public Task<bool> PrintReturnReceiptAsync(
        IEnumerable<VvCash.Models.ReturnReceiptLine> lines, decimal totalRefund, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        Console.WriteLine($"=== RETURN #{documentNumber} ===");
        if (!string.IsNullOrWhiteSpace(saleDate)) Console.WriteLine(saleDate);
        if (!string.IsNullOrWhiteSpace(warehouseName)) Console.WriteLine($"Whse: {warehouseName}");
        if (!string.IsNullOrWhiteSpace(sellerName)) Console.WriteLine($"Seller: {sellerName}");
        foreach (var l in lines)
            Console.WriteLine($"  {l.Name} x{l.Quantity}  {l.LineRefund:F2}");
        Console.WriteLine($"REFUND: {totalRefund:F2}");
        Console.WriteLine("===============");
        return Task.FromResult(true);
    }

    public Task<bool> PrintExchangeReceiptAsync(
        IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        Console.WriteLine($"=== EXCHANGE #{documentNumber} ===");
        if (!string.IsNullOrWhiteSpace(saleDate)) Console.WriteLine(saleDate);
        if (!string.IsNullOrWhiteSpace(warehouseName)) Console.WriteLine($"Whse: {warehouseName}");
        if (!string.IsNullOrWhiteSpace(sellerName)) Console.WriteLine($"Seller: {sellerName}");
        Console.WriteLine("RETURNED:");
        foreach (var l in returned)
            Console.WriteLine($"  {l.Name} x{l.Quantity}  {l.LineRefund:F2}");
        Console.WriteLine("ISSUED:");
        foreach (var l in issued)
            Console.WriteLine($"  {l.Name} x{l.Quantity}  {l.LineRefund:F2}");
        Console.WriteLine(difference > 0 ? $"AMOUNT DUE: {difference:F2}" : $"REFUND: {-difference:F2}");
        Console.WriteLine("===============");
        return Task.FromResult(true);
    }
```

- [ ] **Step 9: Update the two test fakes**

In `tests/VvCash.Tests/ReturnsViewModelTest.cs:60`, replace:

```csharp
        public Task<bool> PrintReturnReceiptAsync(IEnumerable<ReturnReceiptLine> l, decimal t, string d) { Receipt++; return Task.FromResult(true); }
        public Task<bool> PrintExchangeReceiptAsync(IEnumerable<ReturnReceiptLine> returned, IEnumerable<ReturnReceiptLine> issued, decimal difference, string documentNumber) => Task.FromResult(true);
```

with:

```csharp
        public Task<bool> PrintReturnReceiptAsync(IEnumerable<ReturnReceiptLine> l, decimal t, string d, string? warehouseName = null, string? sellerName = null, string? saleDate = null) { Receipt++; return Task.FromResult(true); }
        public Task<bool> PrintExchangeReceiptAsync(IEnumerable<ReturnReceiptLine> returned, IEnumerable<ReturnReceiptLine> issued, decimal difference, string documentNumber, string? warehouseName = null, string? sellerName = null, string? saleDate = null) => Task.FromResult(true);
```

In `tests/VvCash.Tests/ExchangeViewModelTest.cs:150`, replace:

```csharp
        public Task<bool> PrintReturnReceiptAsync(IEnumerable<ReturnReceiptLine> l, decimal t, string d) => Task.FromResult(true);
        public Task<bool> PrintExchangeReceiptAsync(IEnumerable<ReturnReceiptLine> returned, IEnumerable<ReturnReceiptLine> issued, decimal difference, string documentNumber)
        {
            ExchangeReceipt++;
            LastDifference = difference;
            LastDocumentNumber = documentNumber;
            LastIssuedLines = issued.ToList();
            return Task.FromResult(true);
        }
```

with:

```csharp
        public Task<bool> PrintReturnReceiptAsync(IEnumerable<ReturnReceiptLine> l, decimal t, string d, string? warehouseName = null, string? sellerName = null, string? saleDate = null) => Task.FromResult(true);
        public Task<bool> PrintExchangeReceiptAsync(IEnumerable<ReturnReceiptLine> returned, IEnumerable<ReturnReceiptLine> issued, decimal difference, string documentNumber, string? warehouseName = null, string? sellerName = null, string? saleDate = null)
        {
            ExchangeReceipt++;
            LastDifference = difference;
            LastDocumentNumber = documentNumber;
            LastIssuedLines = issued.ToList();
            return Task.FromResult(true);
        }
```

- [ ] **Step 10: Pass the values from both view models**

In `src/VvCash/ViewModels/ReturnsViewModel.cs`, inside `RunPostReturnActionsAsync` (around line 235-241), replace:

```csharp
            var receiptLines = Lines.Where(l => l.ReturnQty > 0)
                .Select(l => new ReturnReceiptLine(l.Name, l.ReturnQty, l.LineRefund));
            try { await _printerService.PrintReturnReceiptAsync(receiptLines, TotalRefund, documentNumber); }
            catch { }
```

with:

```csharp
            var receiptLines = Lines.Where(l => l.ReturnQty > 0)
                .Select(l => new ReturnReceiptLine(l.Name, l.ReturnQty, l.LineRefund));
            try
            {
                await _printerService.PrintReturnReceiptAsync(
                    receiptLines, TotalRefund, documentNumber,
                    SelectedSale?.WarehouseName, SelectedSale?.Creator, SelectedSale?.FormattedSelectedDate);
            }
            catch { }
```

In `src/VvCash/ViewModels/ExchangeViewModel.cs`, inside `RunPostExchangeActionsAsync` (around line 891-896), replace:

```csharp
        if ((_features?.Current.IsEnabled(CashFeatureCodes.ReturnPrintReceipt) ?? true)
            && _printerService != null)
        {
            try { await _printerService.PrintExchangeReceiptAsync(returnedReceiptLines, issuedReceiptLines, difference, documentNumber); }
            catch { }
        }
```

with:

```csharp
        if ((_features?.Current.IsEnabled(CashFeatureCodes.ReturnPrintReceipt) ?? true)
            && _printerService != null)
        {
            try
            {
                await _printerService.PrintExchangeReceiptAsync(
                    returnedReceiptLines, issuedReceiptLines, difference, documentNumber,
                    SelectedSale?.WarehouseName, SelectedSale?.Creator, SelectedSale?.FormattedSelectedDate);
            }
            catch { }
        }
```

- [ ] **Step 11: Fix the raw-ISO date shown on screen**

In `src/VvCash/Views/ExchangeWindow.axaml:71`, replace:

```xml
                <TextBlock Text="{Binding SelectedDate}" FontSize="11" Foreground="{StaticResource Slate500Brush}"/>
```

with:

```xml
                <TextBlock Text="{Binding FormattedSelectedDate}" FontSize="11" Foreground="{StaticResource Slate500Brush}"/>
```

In `src/VvCash/Views/ReturnsWindow.axaml:70`, replace:

```xml
                      <TextBlock Text="{Binding SelectedDate}" FontSize="11" Foreground="{StaticResource Slate500Brush}"/>
```

with:

```xml
                      <TextBlock Text="{Binding FormattedSelectedDate}" FontSize="11" Foreground="{StaticResource Slate500Brush}"/>
```

- [ ] **Step 12: Run every affected test**

Run: `dotnet test tests/VvCash.Tests --filter "FullyQualifiedName~EscPosReturnTest|FullyQualifiedName~EscPosExchangeTest|FullyQualifiedName~ReturnsViewModelTest|FullyQualifiedName~ExchangeViewModelTest"`
Expected: PASS.

- [ ] **Step 13: Manual check in the running app**

Per this repo's XAML bindings being reflective (not compiled), a typo'd `{Binding FormattedSelectedDate}` would build clean and silently show nothing — launch the app, open Returns and Exchange, search a receipt, and confirm the sale-picker card shows a `dd.MM.yyyy HH:mm` date instead of a raw `2026-...Z` string.

- [ ] **Step 14: Commit**

```bash
git add src/VvCash/Models/Api/ReturnModels.cs src/VvCash/Services/Hardware/IPrinterService.cs src/VvCash/Services/Hardware/EscPosPrinterService.cs src/VvCash/Services/Hardware/CompositePrinterService.cs src/VvCash/Services/Hardware/MockPrinterService.cs src/VvCash/ViewModels/ReturnsViewModel.cs src/VvCash/ViewModels/ExchangeViewModel.cs src/VvCash/Views/ExchangeWindow.axaml src/VvCash/Views/ReturnsWindow.axaml tests/VvCash.Tests/EscPosReturnTest.cs tests/VvCash.Tests/EscPosExchangeTest.cs tests/VvCash.Tests/ReturnsViewModelTest.cs tests/VvCash.Tests/ExchangeViewModelTest.cs
git commit -m "feat(receipt): print warehouse, seller and a formatted date on return/exchange receipts"
```

---

## Task 5: scan a barcode to bump the quantity of an already-found returned line

**Files:**
- Modify: `src/VvCash/ViewModels/ReturnLineVm.cs`
- Modify: `src/VvCash/ViewModels/ReturnsViewModel.cs`
- Modify: `src/VvCash/ViewModels/ExchangeViewModel.cs`
- Modify: `src/VvCash/Views/ReturnsWindow.axaml`
- Modify: `src/VvCash/Views/ReturnsWindow.axaml.cs`
- Modify: `src/VvCash/Views/ExchangeWindow.axaml`
- Modify: `src/VvCash/Views/ExchangeWindow.axaml.cs`
- Modify: `src/VvCash/Assets/i18n/{ru,en,kk,tg,uz}.json`
- Test: `tests/VvCash.Tests/ReturnsViewModelTest.cs`
- Test: `tests/VvCash.Tests/ExchangeViewModelTest.cs`

**Design (per the clarified answer):** the receipt is still looked up by its printed number first — that part is unchanged. Once its returnable lines are on screen, a new barcode box lets the cashier scan the physical item instead of hunting for it in a long list: a match bumps that line's `ReturnQty` by one (same effect as pressing `+`) and briefly highlights the card so the cashier can see which row the scan landed on. A miss shows an error, same style as the existing "not found" messages.

- [ ] **Step 1: Add the localized strings**

In `src/VvCash/Assets/i18n/ru.json`, insert before the final `}`:

```json
  "PhoneRequired": "Укажите номер телефона",
  "ScanReturnItem": "Сканируйте товар",
  "BarcodeNotFoundInReceipt": "Товар с этим штрихкодом не найден в этом чеке"
}
```

(This replaces the single-line insertion from Task 2 — if Task 2 already landed, just append the two new keys after `"PhoneRequired"` instead.)

In `src/VvCash/Assets/i18n/en.json`, after `"PhoneRequired": "Enter a phone number",`:

```json
  "ScanReturnItem": "Scan item",
  "BarcodeNotFoundInReceipt": "No item with this barcode in this receipt"
}
```

In `src/VvCash/Assets/i18n/kk.json`, after `"PhoneRequired": "Телефон нөмірін көрсетіңіз",`:

```json
  "ScanReturnItem": "Тауарды сканерлеңіз",
  "BarcodeNotFoundInReceipt": "Бұл түбіртекте бұл штрихкодты тауар табылмады"
}
```

In `src/VvCash/Assets/i18n/tg.json`, after `"PhoneRequired": "Рақами телефонро нишон диҳед",`:

```json
  "ScanReturnItem": "Молро скан кунед",
  "BarcodeNotFoundInReceipt": "Дар ин чек моле бо ин штрихкод ёфт нашуд"
}
```

In `src/VvCash/Assets/i18n/uz.json`, after `"PhoneRequired": "Telefon raqamini kiriting",`:

```json
  "ScanReturnItem": "Mahsulotni skanerlang",
  "BarcodeNotFoundInReceipt": "Ushbu chekda bu shtrix-kodli mahsulot topilmadi"
}
```

- [ ] **Step 2: Add the highlight flag to `ReturnLineVm`**

In `src/VvCash/ViewModels/ReturnLineVm.cs`, add a new observable property next to `_imageBitmap` (after the `ImageBitmap` block, before `public event Action? RefundChanged;`):

```csharp
    /// <summary>True for a brief moment right after a barcode scan matches this
    /// line, so the cashier can see which row among several the scan landed on —
    /// bumping ReturnQty alone is silent and a busy receipt makes it easy to lose
    /// track of which line just changed. Cleared by the view model that set it.</summary>
    [ObservableProperty] private bool _isRecentlyScanned;

```

- [ ] **Step 3: Write the failing test for `ReturnsViewModel`**

Add to `tests/VvCash.Tests/ReturnsViewModelTest.cs`, in the `ReturnsViewModelTest` class (near the other `Lines`-manipulating tests, after `TotalRefund_SumsSelectedLines`):

```csharp
    [Fact]
    public async Task ScanReturnBarcode_MatchingLine_IncrementsItsReturnQty()
    {
        var vm = Build(new FakeReturnService(), new CountingPrinter(), new FakeSettings());
        vm.Lines[0] = new ReturnLineVm(new ReturnDetailLine
        { Product = new ReturnProduct { Id = "pA", Barcode = "111" }, Quantity = 3, QuantityReturned = 0, AfterDiscount = 150 });
        vm.ReturnScanQuery = "111";

        await vm.ScanReturnBarcodeCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Lines[0].ReturnQty);
        Assert.Equal(string.Empty, vm.ReturnScanQuery);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task ScanReturnBarcode_NoMatch_SetsAnErrorAndLeavesQuantitiesAlone()
    {
        var vm = Build(new FakeReturnService(), new CountingPrinter(), new FakeSettings());
        vm.ReturnScanQuery = "does-not-exist";

        await vm.ScanReturnBarcodeCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(0, vm.Lines[0].ReturnQty);
        Assert.Equal(0, vm.Lines[1].ReturnQty);
    }
```

- [ ] **Step 4: Run it to verify it fails**

Run: `dotnet test tests/VvCash.Tests --filter "FullyQualifiedName~ScanReturnBarcode"`
Expected: FAIL to compile — `ReturnScanQuery`/`ScanReturnBarcodeCommand` don't exist yet.

- [ ] **Step 5: Implement the command on `ReturnsViewModel`**

In `src/VvCash/ViewModels/ReturnsViewModel.cs`, add a new observable property next to `_documentNumberQuery` (after its declaration, before `public bool HasSelectedSale`):

```csharp
    /// <summary>What the cashier scanned or typed into the barcode box, once a
    /// receipt's lines are already on screen — a faster way to bump a line's
    /// quantity than hunting for it in a long list.</summary>
    [ObservableProperty] private string _returnScanQuery = string.Empty;
```

Then add the command, right after `SearchSales`/`ClearSearch` (after the `ClearSearch` method, before `partial void OnSelectedSaleChanged`):

```csharp
    /// <summary>Scans the physical item instead of hunting for its line in the
    /// list: a match bumps ReturnQty by one, same as pressing the line's own +
    /// button, and briefly highlights the card so the cashier can see which row
    /// the scan landed on.</summary>
    [RelayCommand]
    private async Task ScanReturnBarcode()
    {
        var code = ReturnScanQuery.Trim();
        ReturnScanQuery = string.Empty;
        if (string.IsNullOrWhiteSpace(code)) return;
        ErrorMessage = null;

        var line = Lines.FirstOrDefault(l => l.IsReturnable && l.Barcode == code);
        if (line == null)
        {
            ErrorMessage = I18nService.Instance["BarcodeNotFoundInReceipt"];
            return;
        }

        line.ReturnQty += 1;
        line.IsRecentlyScanned = true;
        await Task.Delay(700);
        line.IsRecentlyScanned = false;
    }
```

- [ ] **Step 6: Run the `ReturnsViewModel` tests to verify they pass**

Run: `dotnet test tests/VvCash.Tests --filter "FullyQualifiedName~ReturnsViewModelTest"`
Expected: PASS.

- [ ] **Step 7: Repeat for `ExchangeViewModel` — write the failing test**

Add to `tests/VvCash.Tests/ExchangeViewModelTest.cs`. First check how that file builds a view model with `ReturnedLines` already populated (mirror whatever helper it already uses — if none exists, build inline as below):

```csharp
    [Fact]
    public async Task ScanReturnBarcode_MatchingLine_IncrementsItsReturnQty()
    {
        var vm = new ExchangeViewModel();
        vm.SetReturnedLines(new[]
        {
            new ReturnLineVm(new ReturnDetailLine
            { Product = new ReturnProduct { Id = "pA", Barcode = "111" }, Quantity = 3, QuantityReturned = 0, AfterDiscount = 150 }),
        });
        vm.ReturnScanQuery = "111";

        await vm.ScanReturnBarcodeCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.ReturnedLines[0].ReturnQty);
        Assert.Equal(string.Empty, vm.ReturnScanQuery);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task ScanReturnBarcode_NoMatch_SetsAnError()
    {
        var vm = new ExchangeViewModel();
        vm.SetReturnedLines(new[]
        {
            new ReturnLineVm(new ReturnDetailLine
            { Product = new ReturnProduct { Id = "pA", Barcode = "111" }, Quantity = 3, QuantityReturned = 0, AfterDiscount = 150 }),
        });
        vm.ReturnScanQuery = "does-not-exist";

        await vm.ScanReturnBarcodeCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(0, vm.ReturnedLines[0].ReturnQty);
    }
```

- [ ] **Step 8: Run it to verify it fails**

Run: `dotnet test tests/VvCash.Tests --filter "FullyQualifiedName~ExchangeViewModelTest&FullyQualifiedName~ScanReturnBarcode"`
Expected: FAIL to compile.

- [ ] **Step 9: Implement the command on `ExchangeViewModel`**

In `src/VvCash/ViewModels/ExchangeViewModel.cs`, add a new observable property next to `_issuedSearchQuery` (after its declaration):

```csharp
    /// <summary>What the cashier scanned or typed into the returned-side barcode
    /// box, once a receipt's lines are already on screen.</summary>
    [ObservableProperty] private string _returnScanQuery = string.Empty;
```

Then add the command, right after `AddIssuedProduct` (before `IncrementIssued`):

```csharp
    /// <summary>Scans the item being brought back instead of hunting for its line
    /// among the returned ones: a match bumps ReturnQty by one, same as pressing
    /// the line's own + button, and briefly highlights the card.</summary>
    [RelayCommand]
    private async Task ScanReturnBarcode()
    {
        var code = ReturnScanQuery.Trim();
        ReturnScanQuery = string.Empty;
        if (string.IsNullOrWhiteSpace(code)) return;
        ErrorMessage = null;

        var line = ReturnedLines.FirstOrDefault(l => l.IsReturnable && l.Barcode == code);
        if (line == null)
        {
            ErrorMessage = I18nService.Instance["BarcodeNotFoundInReceipt"];
            return;
        }

        line.ReturnQty += 1;
        line.IsRecentlyScanned = true;
        await Task.Delay(700);
        line.IsRecentlyScanned = false;
    }
```

- [ ] **Step 10: Run the `ExchangeViewModel` tests to verify they pass**

Run: `dotnet test tests/VvCash.Tests --filter "FullyQualifiedName~ExchangeViewModelTest"`
Expected: PASS.

- [ ] **Step 11: Add the scan box and highlight style to `ExchangeWindow.axaml`**

In `src/VvCash/Views/ExchangeWindow.axaml`, add a window-level style right after the opening `<Window ...>` tag's closing `>` (before `<Border Background="White" ...>`):

```xml
  <Window.Styles>
    <Style Selector="Border.scanned">
      <Setter Property="BorderBrush" Value="{StaticResource PrimaryBrush}"/>
      <Setter Property="BorderThickness" Value="2"/>
    </Style>
  </Window.Styles>

```

Replace the middle column's outer `Border`/`Grid` (currently `RowDefinitions="Auto,*"`, lines 89-168) — specifically, replace:

```xml
        <Border Grid.Column="1" BorderBrush="{StaticResource Slate100Brush}" BorderThickness="1,0,0,0" Padding="20,16">
          <Grid RowDefinitions="Auto,*">
            <TextBlock Grid.Row="0" Text="{Binding [RefundTotal], Source={x:Static services:I18nService.Instance}}" FontSize="13" FontWeight="Bold" Foreground="{StaticResource Slate500Brush}" Margin="0,0,0,8"/>
            <Grid Grid.Row="1">
```

with:

```xml
        <Border Grid.Column="1" BorderBrush="{StaticResource Slate100Brush}" BorderThickness="1,0,0,0" Padding="20,16">
          <Grid RowDefinitions="Auto,Auto,*">
            <TextBlock Grid.Row="0" Text="{Binding [RefundTotal], Source={x:Static services:I18nService.Instance}}" FontSize="13" FontWeight="Bold" Foreground="{StaticResource Slate500Brush}" Margin="0,0,0,8"/>
            <TextBox Grid.Row="1" x:Name="ReturnScanBox" Text="{Binding ReturnScanQuery}" Classes="IconTextBox"
                     Watermark="{Binding [ScanReturnItem], Source={x:Static services:I18nService.Instance}}"
                     FontSize="14" Margin="0,0,0,12" IsEnabled="{Binding HasSelectedSale}"
                     KeyDown="OnReturnScanKeyDown">
              <TextBox.InnerLeftContent>
                <material:MaterialIcon Kind="BarcodeScan" Width="18" Height="18" Foreground="{StaticResource Slate400Brush}" Margin="10,0,8,0"/>
              </TextBox.InnerLeftContent>
            </TextBox>
            <Grid Grid.Row="2">
```

and change the matching close of that inner `<Grid Grid.Row="1">`'s closing `</Grid>` (the one right before `</Grid>` `</Border>` at lines 166-168) is unaffected — only the opening tag's row index changed, so no further edit is needed there since XAML closing tags don't repeat the attribute.

Add `Classes.scanned="{Binding IsRecentlyScanned}"` to the return-line card's `Border` (line 101):

```xml
                    <Border Background="{StaticResource Slate50Brush}" BorderBrush="{StaticResource Slate200Brush}" BorderThickness="1" CornerRadius="8" Padding="12" Margin="0,4" IsEnabled="{Binding IsReturnable}">
```

becomes:

```xml
                    <Border Classes.scanned="{Binding IsRecentlyScanned}" Background="{StaticResource Slate50Brush}" BorderBrush="{StaticResource Slate200Brush}" BorderThickness="1" CornerRadius="8" Padding="12" Margin="0,4" IsEnabled="{Binding IsReturnable}">
```

- [ ] **Step 12: Wire the Enter key in `ExchangeWindow.axaml.cs`**

Replace the whole file:

```csharp
using Avalonia.Controls;

namespace VvCash.Views;

public partial class ExchangeWindow : Window
{
    public ExchangeWindow()
    {
        InitializeComponent();
    }
}
```

with:

```csharp
using Avalonia.Controls;
using Avalonia.Input;
using VvCash.ViewModels;

namespace VvCash.Views;

public partial class ExchangeWindow : Window
{
    public ExchangeWindow()
    {
        InitializeComponent();
    }

    private void OnReturnScanKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is ExchangeViewModel vm && vm.ScanReturnBarcodeCommand.CanExecute(null))
            vm.ScanReturnBarcodeCommand.Execute(null);
        e.Handled = true;
    }
}
```

(Task 6, below, adds a second handler to this same file — do not remove `OnReturnScanKeyDown` when doing that step.)

- [ ] **Step 13: Repeat for `ReturnsWindow.axaml`**

Add the same `Window.Styles` block right after `<Window ...>`'s closing `>` (before `<Border Background="White" ...>`):

```xml
  <Window.Styles>
    <Style Selector="Border.scanned">
      <Setter Property="BorderBrush" Value="{StaticResource PrimaryBrush}"/>
      <Setter Property="BorderThickness" Value="2"/>
    </Style>
  </Window.Styles>

```

Replace the right column's `Border`/`Grid` (lines 89-91):

```xml
        <Border Grid.Column="1" BorderBrush="{StaticResource Slate100Brush}" BorderThickness="1,0,0,0" Padding="24,16">
          <Grid>
            <TextBlock Text="{Binding [SelectSaleToReturn], Source={x:Static services:I18nService.Instance}}" IsVisible="{Binding !HasSelectedSale}" HorizontalAlignment="Center" VerticalAlignment="Center" FontSize="16" Foreground="{StaticResource Slate400Brush}"/>
            <ItemsControl ItemsSource="{Binding Lines}" IsVisible="{Binding HasSelectedSale}">
```

with:

```xml
        <Border Grid.Column="1" BorderBrush="{StaticResource Slate100Brush}" BorderThickness="1,0,0,0" Padding="24,16">
          <Grid RowDefinitions="Auto,*">
            <TextBox Grid.Row="0" x:Name="ReturnScanBox" Text="{Binding ReturnScanQuery}" Classes="IconTextBox"
                     Watermark="{Binding [ScanReturnItem], Source={x:Static services:I18nService.Instance}}"
                     FontSize="14" Margin="0,0,0,12" IsEnabled="{Binding HasSelectedSale}"
                     KeyDown="OnReturnScanKeyDown">
              <TextBox.InnerLeftContent>
                <material:MaterialIcon Kind="BarcodeScan" Width="18" Height="18" Foreground="{StaticResource Slate400Brush}" Margin="10,0,8,0"/>
              </TextBox.InnerLeftContent>
            </TextBox>
            <Grid Grid.Row="1">
            <TextBlock Text="{Binding [SelectSaleToReturn], Source={x:Static services:I18nService.Instance}}" IsVisible="{Binding !HasSelectedSale}" HorizontalAlignment="Center" VerticalAlignment="Center" FontSize="16" Foreground="{StaticResource Slate400Brush}"/>
            <ItemsControl ItemsSource="{Binding Lines}" IsVisible="{Binding HasSelectedSale}">
```

Immediately after that same block's existing closing (currently just `</Grid>` `</Border>` at lines 116-117), add one more `</Grid>` to close the new wrapping row-grid, so it reads:

```xml
            </ItemsControl>
            </Grid>
          </Grid>
        </Border>
```

Add `Classes.scanned="{Binding IsRecentlyScanned}"` to this window's card `Border` (line 95):

```xml
                  <Border Background="{StaticResource Slate50Brush}" BorderBrush="{StaticResource Slate200Brush}" BorderThickness="1" CornerRadius="8" Padding="14" Margin="0,4" IsEnabled="{Binding IsReturnable}">
```

becomes:

```xml
                  <Border Classes.scanned="{Binding IsRecentlyScanned}" Background="{StaticResource Slate50Brush}" BorderBrush="{StaticResource Slate200Brush}" BorderThickness="1" CornerRadius="8" Padding="14" Margin="0,4" IsEnabled="{Binding IsReturnable}">
```

- [ ] **Step 14: Wire the Enter key in `ReturnsWindow.axaml.cs`**

Replace the whole file:

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

with:

```csharp
using Avalonia.Controls;
using Avalonia.Input;
using VvCash.ViewModels;

namespace VvCash.Views;

public partial class ReturnsWindow : Window
{
    public ReturnsWindow()
    {
        InitializeComponent();
    }

    private void OnReturnScanKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is ReturnsViewModel vm && vm.ScanReturnBarcodeCommand.CanExecute(null))
            vm.ScanReturnBarcodeCommand.Execute(null);
        e.Handled = true;
    }
}
```

- [ ] **Step 15: Build and run every affected test**

Run: `dotnet build src/VvCash -o build/verify` (build to a side output directory — building over a running app's own output locks the files)
Expected: no errors.

Run: `dotnet test tests/VvCash.Tests --filter "FullyQualifiedName~ReturnsViewModelTest|FullyQualifiedName~ExchangeViewModelTest"`
Expected: PASS.

- [ ] **Step 16: Manual check in the running app**

Launch the app (see the [run skill] or however this project is normally started), open Returns, search a receipt with a line that has a barcode, type that barcode into the new box and press Enter — confirm the line's quantity goes up by one and its card briefly gets a colored border. Type a barcode that isn't on the receipt and confirm the error message appears instead. Repeat both checks on the Exchange window's returned-lines column.

- [ ] **Step 17: Commit**

```bash
git add src/VvCash/ViewModels/ReturnLineVm.cs src/VvCash/ViewModels/ReturnsViewModel.cs src/VvCash/ViewModels/ExchangeViewModel.cs src/VvCash/Views/ReturnsWindow.axaml src/VvCash/Views/ReturnsWindow.axaml.cs src/VvCash/Views/ExchangeWindow.axaml src/VvCash/Views/ExchangeWindow.axaml.cs src/VvCash/Assets/i18n/ru.json src/VvCash/Assets/i18n/en.json src/VvCash/Assets/i18n/kk.json src/VvCash/Assets/i18n/tg.json src/VvCash/Assets/i18n/uz.json tests/VvCash.Tests/ReturnsViewModelTest.cs tests/VvCash.Tests/ExchangeViewModelTest.cs
git commit -m "feat(returns): scan a barcode to bump a returned line's quantity"
```

---

## Task 6: Enter/scanner submits the "issued" (exchange-to) product search box

**Files:**
- Modify: `src/VvCash/Views/ExchangeWindow.axaml:176`
- Modify: `src/VvCash/Views/ExchangeWindow.axaml.cs` (adds to the file Task 5 already edited)

**Why:** `AddIssuedProductCommand` (`ExchangeViewModel.cs:438-470`) already does barcode-first lookup, same precedence as the POS cart — only the Enter/scanner trigger on this one `TextBox` is missing; the button click is the only way to fire it today.

- [ ] **Step 1: Name the TextBox and wire KeyDown**

In `src/VvCash/Views/ExchangeWindow.axaml:176`, replace:

```xml
              <TextBox Grid.Column="0" Text="{Binding IssuedSearchQuery}" Classes="IconTextBox"
                       Watermark="{Binding [SearchProductToIssue], Source={x:Static services:I18nService.Instance}}"
                       FontSize="14" VerticalAlignment="Center" Margin="0,0,8,0">
```

with:

```xml
              <TextBox Grid.Column="0" x:Name="IssuedSearchBox" Text="{Binding IssuedSearchQuery}" Classes="IconTextBox"
                       Watermark="{Binding [SearchProductToIssue], Source={x:Static services:I18nService.Instance}}"
                       FontSize="14" VerticalAlignment="Center" Margin="0,0,8,0"
                       KeyDown="OnIssuedSearchKeyDown">
```

- [ ] **Step 2: Add the handler**

In `src/VvCash/Views/ExchangeWindow.axaml.cs` (already edited in Task 5 — this adds one more method, it does not replace the file), add:

```csharp
    private void OnIssuedSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is ExchangeViewModel vm && vm.AddIssuedProductCommand.CanExecute(null))
            vm.AddIssuedProductCommand.Execute(null);
        e.Handled = true;
    }
```

so the file now has both `OnReturnScanKeyDown` and `OnIssuedSearchKeyDown` alongside the constructor.

- [ ] **Step 3: Build**

Run: `dotnet build src/VvCash -o build/verify`
Expected: no errors.

- [ ] **Step 4: Manual check in the running app**

Open Exchange, select a receipt, type or scan a barcode into the "issued goods" search box, press Enter — confirm it adds the line the same way clicking the `+` button next to it already does.

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Views/ExchangeWindow.axaml src/VvCash/Views/ExchangeWindow.axaml.cs
git commit -m "fix(exchange): Enter/scanner now adds the issued-goods search box's match"
```

---

## Task 7: fix long product names overlapping the qty +/− buttons

**Files:**
- Modify: `src/VvCash/Views/ExchangeWindow.axaml:125`
- Modify: `src/VvCash/Views/ReturnsWindow.axaml:98`

**Root cause:** both cards lay the product name out in a `Grid` column marked `*` (share remaining space), immediately followed by `Auto`-width columns for the qty stepper and price — but neither name `TextBlock` sets `TextTrimming`, so a long name renders past the star column's measured width and visually overlaps the buttons to its right. The third card on this screen — `ExchangeWindow.axaml:216`, the "issued goods" side — already has `TextTrimming="CharacterEllipsis"` and does not have this bug; these two get the same fix.

- [ ] **Step 1: Fix the return-line card in `ExchangeWindow.axaml`**

At `src/VvCash/Views/ExchangeWindow.axaml:125`, replace:

```xml
                          <TextBlock Text="{Binding Name}" FontSize="14" FontWeight="Bold" Foreground="{StaticResource Slate900Brush}"/>
```

with:

```xml
                          <TextBlock Text="{Binding Name}" FontSize="14" FontWeight="Bold" Foreground="{StaticResource Slate900Brush}" TextTrimming="CharacterEllipsis"/>
```

- [ ] **Step 2: Fix the card in `ReturnsWindow.axaml`**

At `src/VvCash/Views/ReturnsWindow.axaml:98`, replace:

```xml
                        <TextBlock Text="{Binding Name}" FontSize="15" FontWeight="Bold" Foreground="{StaticResource Slate900Brush}"/>
```

with:

```xml
                        <TextBlock Text="{Binding Name}" FontSize="15" FontWeight="Bold" Foreground="{StaticResource Slate900Brush}" TextTrimming="CharacterEllipsis"/>
```

- [ ] **Step 3: Build**

Run: `dotnet build src/VvCash -o build/verify`
Expected: no errors (XAML-only change, but the reflective-binding caveat doesn't apply here — `TextTrimming` is a plain property, not a binding).

- [ ] **Step 4: Manual check in the running app**

Open Returns (or Exchange) against a receipt containing a product with a long name (or temporarily rename a test product to something long) and confirm the name now ellipsizes instead of overlapping the +/− buttons and price.

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Views/ExchangeWindow.axaml src/VvCash/Views/ReturnsWindow.axaml
git commit -m "fix(returns): stop long product names overlapping the qty buttons"
```

---

## Suggested execution order

Task 1 → Task 2 → Task 3 (backend, can run any time, needed for Task 4's data to actually show up) → Task 4 → Task 5 → Task 6 → Task 7. Tasks 1/2 and 5/6/7 touch disjoint files and could be reordered or parallelized; Task 4 depends on nothing from 1/2/5/6/7 but shares files with Task 5 (`ReturnsViewModel.cs`, `ExchangeViewModel.cs`, the two `.axaml` files) — doing 4 before 5 avoids rebasing scan-related edits around the receipt-header edits.
