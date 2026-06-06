# Product Returns — Design

Date: 2026-06-06
Status: Approved (design)

## Goal

Let a cashier return previously sold products. Browse the sales (expense documents)
list, open a sale, select individual product lines with a return quantity, and submit
a return to the backend. Optionally open the cash drawer and print a return receipt
after a successful return — both behaviors configurable in settings.

## Backend API (verified against live `market.proffi.io`)

All requests go through the existing `AuthHeaderHandler` delegating handler, which
attaches **both** headers used across the app:
- `Cash-Authorization: <CashRegisterToken>`
- `Authorization: Bearer <AuthToken>`

### List sales
`GET /api/v1/documents/expense/?page=<n>`

```json
{
  "body": [
    {
      "selected_date": "2026-06-06T17:32:55.052Z",
      "created_at": "2026-06-06T17:32:55.074858Z",
      "id": "9abd5223-e6b1-4cc2-9075-fb128e0261cf",
      "state": "PROCESSED",
      "creator": "admin admin",
      "counterparty": "UNDEFINED UNDEFINED",
      "document_number": "9",
      "cost": 40, "to_pay": 100, "discount": 0, "payed": 0, "remain": -100
    }
  ],
  "page_count": 1, "total_items": 1, "item_per_page": 10
}
```

### Returnable lines for a sale
`GET /api/v1/documents/return/{expenseId}/`

```json
{
  "message": "success",
  "body": {
    "id": "26f8d6e7-f46d-4431-b23b-8546b07cba54",
    "details": [
      {
        "product": { "id": "6034b45e-daf6-4930-9827-a6fc082dd0dd", "name": "Luxurious Rubber Salad", "barcode": "77191819", "article": "", "...": null },
        "id": "60a02d71-4f0b-4dd5-87f0-869a5d590d4d",
        "quantity": 1,
        "quantity_returned": 1,
        "sold_price": 100,
        "discount_in_unit": 0,
        "after_discount": 100,
        "discount_in_percent": 0
      }
    ]
  },
  "status": 0
}
```

**Returnable quantity per line = `quantity - quantity_returned`.** Lines where this is
0 are fully returned and shown disabled.

### Create return
`POST /api/v1/documents/return/{expenseId}/`

```json
{
  "selected_date": "2026-06-06",
  "details": [ { "product": "6034b45e-daf6-4930-9827-a6fc082dd0dd", "quantity": 1 } ]
}
```

Response is the standard envelope `{ message, body, status }`. **Success = `status == 0`**
(same convention as expense-create). `product` is the product UUID (`product.id` from the
returnable-lines response), not the line `id`.

## Decisions

| Topic | Decision |
|---|---|
| Granularity | Per-line, with a return quantity per line (`<=` returnable). |
| `selected_date` in POST | The sale's **original** `selected_date` (echoed from the list/detail). |
| Offline | **Online-only.** No offline queue — returns need server validation of returnable qty. |
| Post-return actions | Open cash drawer **and** print return receipt, both **configurable** via settings (default on). |
| Entry point | Button in the PosView top-nav / shift area (near Parked Sales / Close Shift). |
| UI flow | Single master-detail window (sales list ↔ returnable lines). |

## Components

### 1. API models — `src/VvCash/Models/Api/ReturnModels.cs`

```
ExpenseListResponse  { body: List<ExpenseListItem>, page_count, total_items, item_per_page }
ExpenseListItem      { selected_date, created_at, id, state, creator, counterparty,
                       document_number, cost, to_pay, discount, payed, remain }
ReturnDetailResponse { message, body: ReturnDetailBody, status }
ReturnDetailBody     { id, details: List<ReturnDetailLine> }
ReturnDetailLine     { product: ReturnProduct, id, quantity, quantity_returned,
                       sold_price, discount_in_unit, after_discount, discount_in_percent }
ReturnProduct        { id, name, barcode, article, ... }
ReturnRequest        { selected_date, details: List<ReturnLineRequest> }
ReturnLineRequest    { product, quantity }
```

`[JsonPropertyName]` on every field matching the API's snake_case, following the existing
`DocumentRequest` model conventions. Monetary fields use `decimal`.

### 2. Service — `src/VvCash/Services/Api/IReturnService.cs` + `ReturnService.cs`

```csharp
Task<ExpenseListResponse> GetSalesAsync(int page = 1);            // GET expense/?page=
Task<ReturnDetailBody>    GetReturnableLinesAsync(string expenseId); // GET return/{id}/
Task<bool>                CreateReturnAsync(string expenseId, ReturnRequest request); // POST return/{id}/
```

- `GetBaseUrl()` copied from `ExpenseDocumentService` (reads `ISettingsService.BackendUrl`,
  ensures trailing slash).
- Online-only: network exception or `status != 0` → throw a typed/result error the
  ViewModel surfaces; **no** offline persistence.
- Distinguish "no connection" from "server rejected" so the UI can show the right message.

### 3. ViewModels — `src/VvCash/ViewModels/`

`ReturnsViewModel(Window dialog, IReturnService, IPrinterService, ISettingsService)`:
- `ObservableCollection<ExpenseListItem> Sales`, paging (`CurrentPage`, `PageCount`,
  next/prev/load commands).
- `SelectedSale` → loads returnable lines into `Lines`.
- `ObservableCollection<ReturnLineVm> Lines`.
- `SubmitReturnCommand` — builds `ReturnRequest` from lines with `ReturnQty > 0`,
  `selected_date` = `SelectedSale.SelectedDate` (original). On `status:0`:
  if `ReturnOpenCashDrawer` → `OpenCashDrawerAsync`; if `ReturnPrintReceipt` →
  `PrintReturnReceiptAsync`; then success toast + refresh lines/list.
- `IsBusy`, `ErrorMessage`, `TotalRefund` (sum of `ReturnQty * after_discount`).

`ReturnLineVm` (own small class):
- `ProductId, Name, Barcode, SoldQty, AlreadyReturned, MaxReturnable (= SoldQty - AlreadyReturned),
  ReturnQty` (clamped `0..MaxReturnable`), `SoldPrice`, `IsReturnable (MaxReturnable > 0)`.
- `IncrementCommand` / `DecrementCommand` **defined on `ReturnLineVm` itself** so the row
  DataTemplate binds directly to the row's DataContext. Do **not** cast the ancestor
  DataContext to the window VM type inside the item template — it compiles but crashes at
  runtime (known Avalonia pitfall).

### 4. View — `src/VvCash/Views/ReturnsWindow.axaml` (+ minimal `.axaml.cs`)

Mirror `ParkedSalesWindow`: Material theme, `I18nService.Instance[key]` bindings.
Two-pane `Grid`:
- Left: paged sales list (document number, date, creator, counterparty, total, state) + paging controls.
- Right: returnable lines for the selected sale — product name, sold qty, already-returned,
  returnable max, qty stepper (`-`/value/`+`), per-line refund; footer with total refund and
  the **Return** button.
- States: loading, empty (no sales), error banner, and disabled rows for fully-returned lines.
- **Return** button disabled when no line has `ReturnQty > 0`.

### 5. Entry point — `PosViewModel` + `PosView.axaml`

- Inject `IReturnService` into `PosViewModel` constructor.
- `[RelayCommand] OpenReturns` — opens `ReturnsWindow` via `ShowDialog(mainWindow)`,
  `DataContext = new ReturnsViewModel(dialog, _returnService, _printerService, _settingsService)`.
  Mirrors the existing `OpenParkedSales` command.
- Add a `NavButton` in the PosView top-nav / shift area bound to `OpenReturnsCommand`.

### 6. Configurable post-return actions

Settings (`ISettingsService` / `SettingsService`, persisted to the existing settings JSON):
- `bool ReturnOpenCashDrawer` (default `true`)
- `bool ReturnPrintReceipt` (default `true`)
- Surface both as toggles in `SettingsView` / `SettingsViewModel`.

Cash drawer (new — none exists today):
- Add `Task<bool> OpenCashDrawerAsync()` to `IPrinterService`.
- `EscPosPrinterService`: send the standard kick pulse `0x1B, 0x70, 0x00, 0x19, 0xFA`
  (`ESC p m t1 t2`).
- `MockPrinterService` + `CompositePrinterService`: implement (mock logs; composite delegates).

Return receipt (new):
- Add `Task<bool> PrintReturnReceiptAsync(IEnumerable<ReturnLineVm> lines, decimal totalRefund, string documentNumber)`
  to `IPrinterService`, with a "RETURN / ВОЗВРАТ" header to distinguish it from a sale receipt.
- Implement in EscPos / Mock / Composite.

### 7. DI — `src/VvCash/App.axaml.cs`

```csharp
services.AddHttpClient<IReturnService, ReturnService>()
        .AddHttpMessageHandler<AuthHeaderHandler>();
```
Register near the existing `ExpenseDocumentService` HTTP-client registration.

### 8. i18n — `src/VvCash/Assets/i18n/{en,ru,uz,tg,kk}.json`

New keys (all 5 locales): window title, Return action, Returnable, Already returned,
Sold qty, Refund total, No sales, Return success, Return failed, No connection,
Open cash drawer (setting), Print return receipt (setting).

## Error handling

- Online-only: network failure → error banner ("no connection", i18n); dialog stays open.
- API `status != 0` / non-2xx → show the server `body`/message from the envelope.
- `ReturnQty` clamped to `[0, MaxReturnable]`; **Return** disabled when nothing selected.
- Fully-returned lines (`MaxReturnable == 0`) shown disabled/grayed.
- Drawer/print failures are non-fatal: the return already succeeded server-side; log + toast,
  don't roll back.

## Testing

Unit:
- `ReturnService` (de)serialization against the captured live samples (list, return-detail,
  POST envelope).
- `ReturnLineVm` clamp logic (0..Max, increment/decrement bounds).
- Request building: only `ReturnQty > 0` lines included; `product` = product UUID;
  `selected_date` passthrough from original sale.

Manual:
- Build to `build/verify` to avoid the running-app file lock (project build-lock note).
- Open Returns from the shift/nav area, return 1 unit of a sale, confirm POST `status:0`,
  and verify drawer + print honor the settings toggles.

## Out of scope

- Offline return queue / sync.
- Editing or voiding an entire sale (only line-level returns).
- Refund-to-card flows (cash drawer only for the configurable refund action).
