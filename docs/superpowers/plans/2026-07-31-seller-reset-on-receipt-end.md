# Сброс продавца по завершении чека — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Завершённая операция на кассе (успешная оплата, ручная очистка чека, проведённый возврат или обмен) обнуляет текущего продавца, чтобы следующий чек нельзя было пробить под чужим именем, не дожидаясь 90-секундного идл-таймаута.

**Architecture:** Одна приватная точка `PosViewModel.EndReceipt()` зовёт уже существующий `ISellerSession.Clear()`; её вызывают четыре места. Модальные диалоги возврата и обмена сообщают о фактически записанном документе липким свойством `CompletedDocument`, которое `PosViewModel` читает после `await ShowDialog`. Ни `SellerSession`, ни API, ни бэкенд не меняются.

**Tech Stack:** .NET 8 / C#, Avalonia (MVVM, CommunityToolkit.Mvvm), xUnit, ручные фейки без mock-библиотеки.

**Spec:** [docs/superpowers/specs/2026-07-31-seller-reset-on-receipt-end-design.md](../specs/2026-07-31-seller-reset-on-receipt-end-design.md)

---

## Файлы

| Файл | Что делает |
| --- | --- |
| `src/VvCash/ViewModels/PosViewModel.cs` | новый `EndReceipt()`; вызовы из success-ветки `Pay`, из `ClearCart`, после диалогов возврата и обмена |
| `src/VvCash/ViewModels/ReturnsViewModel.cs` | новое `CompletedDocument` |
| `src/VvCash/ViewModels/ExchangeViewModel.cs` | новое `CompletedDocument` |
| `tests/VvCash.Tests/PosViewModelSellerGateTest.cs` | тесты сброса после оплаты и очистки чека; knob `CreateResult` у фейка документов |
| `tests/VvCash.Tests/ReturnsViewModelTest.cs` | тесты флага возврата; knob `CreateResult` у фейка возвратов |
| `tests/VvCash.Tests/ExchangeViewModelTest.cs` | тесты флага обмена |

Запуск тестов — всегда `& ./run-tests.ps1` из корня репозитория (сборка уходит в `build/verify-tests`, чтобы запущенное приложение не держало lock на выходной каталог). `pwsh` на машине нет — вызывать именно через `&`.

---

### Task 1: `EndReceipt()` и сброс после успешной оплаты

**Files:**
- Modify: `tests/VvCash.Tests/PosViewModelSellerGateTest.cs` (фейк `FakeExpenseDocumentService`, ~строка 347; новые тесты в конец класса)
- Modify: `src/VvCash/ViewModels/PosViewModel.cs` (новый метод рядом с `PerformSignOut`, ~строка 519; success-ветка `Pay`, ~строка 1759)

- [ ] **Step 1: Добавить knob провала в фейк документов**

В `tests/VvCash.Tests/PosViewModelSellerGateTest.cs` заменить тело `FakeExpenseDocumentService.CreateExpenseDocumentAsync` — сейчас оно всегда возвращает `true`, а тесту нужен провал создания документа:

```csharp
    private class FakeExpenseDocumentService : IExpenseDocumentService
    {
        public DocumentRequest? LastRequest { get; private set; }

        /// <summary>What CreateExpenseDocumentAsync reports back — defaults to success
        /// (matching prior behaviour). The end-of-receipt tests flip it to false to
        /// exercise the failed-payment branch, where the seller must survive so a retry
        /// doesn't demand a fresh PIN.</summary>
        public bool CreateResult { get; set; } = true;

        public Task<bool> CreateExpenseDocumentAsync(DocumentRequest request)
        {
            LastRequest = request;
            return Task.FromResult(CreateResult);
        }
```

Остальные члены фейка (`CreateExpenseDocumentDetailedAsync`, `SyncOfflineDocumentsAsync`, `GetUnsyncedDocumentsCountAsync`, события, `RaiseSessionRevoked`) не трогать.

- [ ] **Step 2: Написать падающие тесты**

В конец класса `PosViewModelSellerGateTest` (перед закрывающей `}` файла) добавить:

```csharp
    // ---------------------------------------------------------------------------------
    // End of receipt: a finished operation drops the confirmed seller outright, so the
    // next receipt cannot be rung up under the previous person's name inside the idle
    // window. See docs/superpowers/specs/2026-07-31-seller-reset-on-receipt-end-design.md.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void Pay_OnSuccess_ClearsCurrentSeller()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1"));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated =>
        {
            if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m;
        };

        vm.PayCommand.Execute(null);
        Assert.NotNull(mixedPaymentVm);

        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        Assert.Null(deps.SellerSession.Current);
    }

    [Fact]
    public void Pay_WhenDocumentCreationFails_KeepsCurrentSeller()
    {
        // A failed payment is not the end of a receipt: the cashier is expected to try
        // again, and demanding a fresh PIN for a retry would punish the wrong person.
        using var vm = CreateViewModel(out var deps);
        deps.ExpenseDocumentService.CreateResult = false;
        var seller = MakeSeller("s1");
        deps.SellerSession.SetCurrent(seller);
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated =>
        {
            if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m;
        };

        vm.PayCommand.Execute(null);
        Assert.NotNull(mixedPaymentVm);

        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        Assert.Same(seller, deps.SellerSession.Current);
    }
```

- [ ] **Step 3: Убедиться, что тесты падают**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~PosViewModelSellerGateTest.Pay_OnSuccess_ClearsCurrentSeller|FullyQualifiedName~PosViewModelSellerGateTest.Pay_WhenDocumentCreationFails_KeepsCurrentSeller"`

Expected: `Pay_OnSuccess_ClearsCurrentSeller` — FAIL (`Assert.Null() Failure`, продавец всё ещё выбран). `Pay_WhenDocumentCreationFails_KeepsCurrentSeller` — PASS (сброса пока нет вообще; тест зафиксирован как регресс-защита на следующий шаг).

- [ ] **Step 4: Добавить `EndReceipt()`**

В `src/VvCash/ViewModels/PosViewModel.cs` сразу после метода `PerformSignOut` (заканчивается на `LogoutRequested?.Invoke(this, explanation);` + `}`) вставить:

```csharp
    /// <summary>Single choke point for "this receipt is over — nobody is confirmed any
    /// more". Called from every place an operation actually finishes: a successful
    /// payment, the cashier manually clearing the receipt, and a returns/exchange dialog
    /// that genuinely booked a document. The idle timeout stays as a second line of
    /// defence for a receipt abandoned halfway; this one closes the window where the next
    /// person starts ringing up within 90 seconds and their sale is silently credited to
    /// whoever sold last (see the 2026-07-31 spec).
    ///
    /// Kept as one method rather than four inline Clear() calls for the same reason
    /// PerformSignOut above is one method: the next end-of-receipt path added to this
    /// class must have one obvious place to hook into, or it will quietly skip the reset.
    ///
    /// No IsSellerSwitchEnabled guard on purpose: with switching off nobody ever becomes
    /// Current, and SellerSession.Clear() returns early when Current is already null, so
    /// this degrades to a no-op on its own.</summary>
    private void EndReceipt() => _sellerSession.Clear();
```

- [ ] **Step 5: Позвать `EndReceipt()` из success-ветки оплаты**

В том же файле, в success-ветке `Pay` (внутри `if (success)`, где уже стоят `_cartService.ClearCart()`, `SelectedCustomer = null;`, `_approvedById = null;`), сразу после `_approvedById = null;` добавить:

```csharp
                        // The receipt is done and the document (posted or queued offline)
                        // already carries this seller's id — from here on nobody is
                        // confirmed. Only on this success branch: see EndReceipt.
                        EndReceipt();
```

- [ ] **Step 6: Прогнать тесты**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~PosViewModelSellerGateTest"`

Expected: PASS, включая уже существующие `Pay_WithCurrentSeller_StampsSellerIdOntoRequest` и `Pay_WithNoCurrentSeller_OmitsSellerIdFromRequestAndJson` (они читают `LastRequest` после оплаты, а не `Current`, так что сброс их не ломает).

- [ ] **Step 7: Коммит**

```bash
git add src/VvCash/ViewModels/PosViewModel.cs tests/VvCash.Tests/PosViewModelSellerGateTest.cs
git commit -m "feat(seller): drop the confirmed seller once a payment goes through"
```

---

### Task 2: Сброс при ручной очистке чека

**Files:**
- Modify: `tests/VvCash.Tests/PosViewModelSellerGateTest.cs` (новые тесты в конец класса)
- Modify: `src/VvCash/ViewModels/PosViewModel.cs` (команда `ClearCart`, ~строка 1199)

- [ ] **Step 1: Написать падающие тесты**

Добавить в конец класса `PosViewModelSellerGateTest`:

```csharp
    [Fact]
    public void ClearCart_ClearsCurrentSeller()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1"));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));

        vm.ClearCartCommand.Execute(null);

        Assert.Null(deps.SellerSession.Current);
    }

    [Fact]
    public void AddToCart_AfterReceiptEnded_AsksAgainWithoutWaitingOutTheIdleTimeout()
    {
        // The whole point of the reset: the idle clock has NOT elapsed (AddToCart's own
        // Touch() just reset it), and the gate must still fire for the next receipt.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1"));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        vm.ClearCartCommand.Execute(null);
        Assert.False(deps.SellerSession.TimedOut);

        vm.AddToCartCommand.Execute(MakeProduct("p2", 10m));

        Assert.Equal(1, raisedCount);
    }

    [Fact]
    public void ClearCart_WithNobodyConfirmed_IsANoOp_RaisesNoCurrentChanged()
    {
        // The seller-switching-off case: nobody ever becomes Current there, and the reset
        // must degrade to nothing rather than churn the chip through CurrentChanged.
        using var vm = CreateViewModel(out var deps);
        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));
        var chipChanges = 0;
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(PosViewModel.SellerChipText)) chipChanges++;
        };

        vm.ClearCartCommand.Execute(null);

        Assert.Null(deps.SellerSession.Current);
        Assert.Equal(0, chipChanges);
    }
```

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~PosViewModelSellerGateTest.ClearCart|FullyQualifiedName~PosViewModelSellerGateTest.AddToCart_AfterReceiptEnded_AsksAgainWithoutWaitingOutTheIdleTimeout"`

Expected: 2 failed, 1 passed. `ClearCart_ClearsCurrentSeller` падает на `Assert.Null()`; `AddToCart_AfterReceiptEnded_...` — на `Assert.Equal(1, raisedCount)` (получено 0: продавец ещё выбран, значит `IsStale` ложно и гейт молчит). `ClearCart_WithNobodyConfirmed_IsANoOp_RaisesNoCurrentChanged` проходит сразу — это регресс-защита на следующий шаг.

- [ ] **Step 3: Позвать `EndReceipt()` из `ClearCart`**

В `src/VvCash/ViewModels/PosViewModel.cs` команду `ClearCart` привести к виду:

```csharp
    [RelayCommand]
    private void ClearCart()
    {
        _cartService.ClearCart();
        _cartService.ClearCustomerDiscount();
        SelectedCustomer = null;
        ClearActivePromo();
        _approvedById = null;
        _ = _customerDisplayService.ClearAsync();

        // Only this command — the cashier deliberately dropping the receipt. The
        // internal _cartService.ClearCart() calls (park, auto-park inside
        // ResumeParkedSale) are mid-operation, not the end of one, and must not reset.
        EndReceipt();
    }
```

- [ ] **Step 4: Прогнать тесты**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~PosViewModelSellerGateTest"`

Expected: PASS.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/ViewModels/PosViewModel.cs tests/VvCash.Tests/PosViewModelSellerGateTest.cs
git commit -m "feat(seller): drop the confirmed seller when the cashier clears the receipt"
```

---

### Task 3: `ReturnsViewModel.CompletedDocument`

**Files:**
- Modify: `tests/VvCash.Tests/ReturnsViewModelTest.cs` (фейк `FakeReturnService`, ~строка 16; новые тесты в конец класса)
- Modify: `src/VvCash/ViewModels/ReturnsViewModel.cs` (свойство рядом с остальными; `SubmitReturn`, ~строка 150)

- [ ] **Step 1: Добавить knob провала в фейк возвратов**

В `tests/VvCash.Tests/ReturnsViewModelTest.cs` заменить `FakeReturnService.CreateReturnAsync`:

```csharp
    private sealed class FakeReturnService : IReturnService
    {
        public ReturnRequest? LastRequest;
        public string? LastExpenseId;

        /// <summary>What CreateReturnAsync reports back — defaults to success (matching
        /// prior behaviour); the CompletedDocument tests flip it to false.</summary>
        public bool CreateResult = true;

        public Task<ExpenseListResponse> GetSalesAsync(int page = 1)
            => Task.FromResult(new ExpenseListResponse());
        public Task<ReturnDetailBody> GetReturnableLinesAsync(string expenseId)
            => Task.FromResult(new ReturnDetailBody());
        public Task<bool> CreateReturnAsync(string expenseId, ReturnRequest request)
        {
            LastExpenseId = expenseId; LastRequest = request;
            return Task.FromResult(CreateResult);
        }
    }
```

- [ ] **Step 2: Написать падающие тесты**

В конец класса `ReturnsViewModelTest` добавить:

```csharp
    [Fact]
    public async Task SubmitReturn_OnSuccess_MarksCompletedDocument()
    {
        // PosViewModel reads this after the modal closes to decide whether the register
        // just finished an operation and must re-ask who is selling.
        var svc = new FakeReturnService();
        var vm = Build(svc, new CountingPrinter(), new FakeSettings());
        vm.Lines[0].ReturnQty = 1;

        await vm.SubmitReturnCommand.ExecuteAsync(null);

        Assert.True(vm.CompletedDocument);
    }

    [Fact]
    public async Task SubmitReturn_WhenServerRejects_LeavesCompletedDocumentFalse()
    {
        // Nothing was booked, so opening and closing the screen must not cost a PIN.
        var svc = new FakeReturnService { CreateResult = false };
        var vm = Build(svc, new CountingPrinter(), new FakeSettings());
        vm.Lines[0].ReturnQty = 1;

        await vm.SubmitReturnCommand.ExecuteAsync(null);

        Assert.False(vm.CompletedDocument);
    }
```

`vm.Lines[0].ReturnQty = 1;` обязателен: без него `CanSubmit` ложно и `SubmitReturn` выходит на первой строке, ничего не вызвав. Хелпер `Build` уже есть в этом файле и сам заполняет `SelectedSale` и две строки возврата — соседние тесты зовут его точно так же.

- [ ] **Step 3: Убедиться, что тесты не компилируются/падают**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~ReturnsViewModelTest"`

Expected: ошибка сборки `CS1061: 'ReturnsViewModel' does not contain a definition for 'CompletedDocument'`.

- [ ] **Step 4: Добавить свойство и его выставление**

В `src/VvCash/ViewModels/ReturnsViewModel.cs` добавить свойство рядом с остальными публичными членами класса (над `SubmitReturn`):

```csharp
    /// <summary>True once this screen has actually booked a return on the server. Read by
    /// PosViewModel after the modal closes: a screen that was opened and closed without
    /// booking anything is not the end of an operation and must not cost the cashier a
    /// fresh PIN. Sticky — several returns in one sitting are still "a document happened".</summary>
    public bool CompletedDocument { get; private set; }
```

В `SubmitReturn`, сразу после блока `if (!ok) { ... return; }` и перед `await RunPostReturnActionsAsync(...)`:

```csharp
            // Set before the drawer/receipt side effects, not after: those are
            // best-effort (they swallow their own exceptions) and the document is already
            // on the server by this point regardless of how printing goes.
            CompletedDocument = true;
```

- [ ] **Step 5: Прогнать тесты**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~ReturnsViewModelTest"`

Expected: PASS.

- [ ] **Step 6: Коммит**

```bash
git add src/VvCash/ViewModels/ReturnsViewModel.cs tests/VvCash.Tests/ReturnsViewModelTest.cs
git commit -m "feat(returns): report whether the screen actually booked a return"
```

---

### Task 4: `ExchangeViewModel.CompletedDocument`

**Files:**
- Modify: `tests/VvCash.Tests/ExchangeViewModelTest.cs` (новые тесты в конец класса)
- Modify: `src/VvCash/ViewModels/ExchangeViewModel.cs` (свойство рядом с остальными публичными членами; `SubmitExchange`, ~строка 466)

- [ ] **Step 1: Написать падающие тесты**

В конец класса `ExchangeViewModelTest` добавить:

```csharp
    [Fact]
    public async Task SubmitExchange_OnSuccess_MarksCompletedDocument()
    {
        var rig = BuildForSubmit();

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.True(rig.Vm.CompletedDocument);
    }

    [Fact]
    public async Task SubmitExchange_ReturnBookedButPayoutFailed_StillMarksCompletedDocument()
    {
        // The return cannot be cancelled, so a document exists even though the exchange
        // never finished — the register has done something and must re-ask who is selling.
        var rig = BuildForSubmit();
        rig.Payout.Outcome = CashOpOutcome.Failed("cash balance would go negative");

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.NotNull(rig.Vm.ErrorMessage);
        Assert.True(rig.Vm.CompletedDocument);
    }

    [Fact]
    public async Task SubmitExchange_WhenTheReturnItselfFails_LeavesCompletedDocumentFalse()
    {
        // Nothing reached the server at all.
        var rig = BuildForSubmit();
        rig.Returns.Result = false;

        await rig.Vm.SubmitExchangeCommand.ExecuteAsync(null);

        Assert.False(rig.Vm.CompletedDocument);
    }
```

- [ ] **Step 2: Убедиться, что тесты не компилируются/падают**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~ExchangeViewModelTest"`

Expected: ошибка сборки `CS1061: 'ExchangeViewModel' does not contain a definition for 'CompletedDocument'`.

- [ ] **Step 3: Добавить свойство и его выставление**

В `src/VvCash/ViewModels/ExchangeViewModel.cs` рядом с остальными публичными членами класса добавить:

```csharp
    /// <summary>True once this screen has written anything to the server — that is, from
    /// the moment the return leg is booked, since a return cannot be cancelled and the
    /// remaining legs may still fail. Read by PosViewModel after the modal closes to
    /// decide whether an operation actually happened and the seller must be re-confirmed.
    /// Sticky, exactly like ReturnsViewModel.CompletedDocument.</summary>
    public bool CompletedDocument { get; private set; }
```

В `SubmitExchange`, в блоке шага 1 (`// ---- 1. the return ----`), рядом с уже существующим `_returnBooked = true;`:

```csharp
                _returnBooked = true;
                CompletedDocument = true;
```

- [ ] **Step 4: Прогнать тесты**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~ExchangeViewModelTest"`

Expected: PASS.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/ViewModels/ExchangeViewModel.cs tests/VvCash.Tests/ExchangeViewModelTest.cs
git commit -m "feat(exchange): report whether the screen wrote any document"
```

---

### Task 5: Подключить диалоги возврата и обмена

**Files:**
- Modify: `src/VvCash/ViewModels/PosViewModel.cs` (`ShowReturnsDialogAsync`, ~строка 1548; `OpenExchange`, ~строка 1562)

Автоматического теста здесь нет и не будет: обе точки создают `Window` и зовут `ShowDialog`, что недоступно без запущенного Avalonia-приложения — тот же разрыв покрытия, который уже описан в шапке `PosViewModelSellerGateTest`. Проверка — компиляция плюс тесты флагов из Task 3/4, которые доказывают, что читаемое значение верное.

- [ ] **Step 1: Читать флаг после диалога возврата**

В `ShowReturnsDialogAsync` заменить создание/показ диалога на:

```csharp
                var dialog = new VvCash.Views.ReturnsWindow();
                var returnsVm = new ReturnsViewModel(dialog, _returnService, _printerService, _settingsService, _features);
                dialog.DataContext = returnsVm;
                await dialog.ShowDialog(mainWindow);

                // A returns screen that actually booked something ends the operation the
                // same way a payment does — see EndReceipt. Opened and closed without a
                // return, it costs nothing.
                if (returnsVm.CompletedDocument) EndReceipt();
```

- [ ] **Step 2: Читать флаг после диалога обмена**

В `OpenExchange` заменить создание/показ диалога на:

```csharp
                var dialog = new VvCash.Views.ExchangeWindow();
                var exchangeVm = new ExchangeViewModel(
                    dialog, _returnService, _cashOperationService, _expenseDocumentService,
                    _counterpartyService, _settingsService, _productService, _syncService,
                    _printerService, _features,
                    _promotionProvider.MoneyPolicy, CurrentShiftId ?? string.Empty,
                    _sellerSession.Current?.Id, _session.CashId, IsSystemOnline);
                dialog.DataContext = exchangeVm;
                await dialog.ShowDialog(mainWindow);

                // Same rule as returns above. The seller id the exchange documents carry
                // was snapshotted at construction time, so clearing here cannot affect
                // what was already sent.
                if (exchangeVm.CompletedDocument) EndReceipt();
```

- [ ] **Step 3: Прогнать весь набор тестов**

Run: `& ./run-tests.ps1`

Expected: PASS, 0 failed.

- [ ] **Step 4: Собрать приложение**

Run: `dotnet build src/VvCash/VvCash.csproj -c Debug -o build/verify`

Expected: `Build succeeded`, 0 errors. Каталог `build/verify` — обход блокировки выходных файлов запущенным приложением; `build/` в `.gitignore`.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/ViewModels/PosViewModel.cs
git commit -m "feat(seller): re-ask who is selling after a return or exchange books a document"
```
