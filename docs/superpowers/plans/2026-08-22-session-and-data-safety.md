# Session and Data Safety (code-review batch A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the test suite deterministic, then close three findings from the full code review — `ShiftService` never recognising the 403 the backend actually sends for a rejected session, `CustomerDisplayWindow` accumulating one window per login while ignoring its own feature flag, and the settings screen destroying the unsynced-sales queue in one unconfirmed tap.

**Architecture:** One repo, `C:\work\vv-cash`. No backend changes. `IShiftService` gains a second event so 401 (unambiguous, auto sign-out) and 403 (ambiguous, explain and let the cashier decide) stay apart. `App.axaml.cs` stops building a customer-display window per navigation and keeps one for the whole run, its visibility driven by a new `PosViewModel` event; which screen it lands on — and whether it is created at all on a single-monitor dev box — is decided by a new pure selector modelled on `RenderingSelector`. The settings screen loses its queue-wiping button outright and gains a confirmation overlay copied in shape from `PosViewModel`'s existing shift-close confirm.

**Tech Stack:** C# / .NET 10, Avalonia UI 11.2.3 (reflective bindings — `AvaloniaUseCompiledBindingsByDefault=false`, so a typo'd binding path compiles clean and fails silently; XAML must be verified by running the app), CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`), xUnit.

**Design:** [2026-08-22-session-and-data-safety-design.md](../specs/2026-08-22-session-and-data-safety-design.md)

**Correspondence to the review findings:**
1. `ShiftService` doesn't recognise 403 → Tasks 1, 2, 3
2. `CustomerDisplayWindow` leaks and ignores its feature flag → Tasks 4, 5, 6

Task 0 is not from the review: it fixes a self-inflicted race in the test suite that would
otherwise make every verification step below unreadable. See its own section.
3. Settings destroys the unsynced queue with no confirmation → Tasks 7, 8

**Test baseline: 678 tests, all passing — but only after Task 0.**

Before Task 0 the suite is non-deterministic. Three consecutive runs on an unchanged tree
gave 678/678, then 677/1, then 676/2, each failure landing on a *different* test and always
inside `Avalonia.Threading.DispatcherPriorityQueue`. The cause is ours, not Avalonia's:
`Dispatcher.UIThread` is a process-wide singleton, four test classes pump it with
`RunJobs()`, and xunit's default parallelises across collections (one per class). Task 0
serialises the suite so every task after it has a signal worth reading.

The per-task expected counts are 678 plus the tests each task adds: Task 1 adds 6, Task 2
adds 3, Task 4 adds 12, Task 5 adds 2, Task 8 adds 5 — ending at 706 passing, zero failing. Tasks 3, 6, 7 and 9 add none. A count that is off
by a fixed amount from the start means the baseline moved rather than the task failing;
re-check with `git stash` before hunting a bug.

**Running tests:** `& ./run-tests.ps1` from the repo root (PowerShell). Not `pwsh` — it is
not installed on this machine. The script builds to `build/verify-tests` so a running
register cannot lock the output.

---

## Task 0: Make the test suite deterministic

**Files:**
- Create: `tests/VvCash.Tests/AssemblyInfo.cs`

**Root cause:** `Avalonia.Threading.Dispatcher.UIThread` is a process-wide singleton, and
four test classes drive it with `RunJobs()` — `ExpenseDocumentServiceTest`,
`PosViewModelSellerGateTest`, `ShiftServiceTest`, `UpdateViewModelTest`. The project has no
xunit configuration, so the default is in force: test collections run in parallel, one
collection per class. Those four therefore pump the same queue concurrently and corrupt its
priority chain. Three consecutive runs on an unchanged tree produced 678/678, 677/1 and
676/2, the failure landing on a different test each time.

This has to be first. Three of the tasks that follow add tests that pump the dispatcher,
and every task's verification step compares a test count — none of which is worth anything
against a baseline that moves on its own.

- [ ] **Step 1: Confirm the flake exists before fixing it**

```powershell
1..5 | ForEach-Object { & ./run-tests.ps1 --no-build 2>&1 | Select-String 'Passed!|Failed!|пройден' }
```

Expected: at least one run reports a failure, and the failing test is not always the same
one. If all five runs are clean, run five more — the race does not fire on every pass. Note
which tests failed; you will confirm the same ones pass reliably in Step 4.

- [ ] **Step 2: Serialise the suite**

Create `tests/VvCash.Tests/AssemblyInfo.cs`:

```csharp
using Xunit;

// Avalonia's Dispatcher.UIThread is a process-wide singleton, and four classes in this
// suite drive it with RunJobs() to prove their subjects marshal correctly:
// ExpenseDocumentServiceTest, PosViewModelSellerGateTest, ShiftServiceTest and
// UpdateViewModelTest. xunit's default runs collections in parallel with one collection
// per class, so those four raced each other inside DispatcherPriorityQueue and a varying
// test failed on roughly two runs in three — a baseline nobody could read a real
// regression against.
//
// Serialising the whole assembly rather than grouping the four into one [Collection]:
// the suite finishes in one to two seconds either way, so the parallelism buys nothing
// measurable, and a per-class opt-in is a rule the next dispatcher-touching test has to
// remember to join. This one cannot be forgotten.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
```

- [ ] **Step 3: Run the suite once to confirm it builds and passes**

```powershell
& ./run-tests.ps1
```

Expected: 678 passed, 0 failed.

- [ ] **Step 4: Run it five more times to confirm determinism**

```powershell
1..5 | ForEach-Object { & ./run-tests.ps1 --no-build 2>&1 | Select-String 'Passed!|Failed!|пройден' }
```

Expected: five identical clean runs, 678 passed and 0 failed every time. If any run still
fails inside `DispatcherPriorityQueue`, stop and report — the attribute did not take
effect, and nothing after this task can be trusted.

- [ ] **Step 5: Commit**

```bash
git add tests/VvCash.Tests/AssemblyInfo.cs
git commit -m "test: stop the suite racing itself through Avalonia's dispatcher

Dispatcher.UIThread is a process-wide singleton and four test classes pump it
with RunJobs(). With xunit's default collection parallelism those four ran at the
same time and corrupted the priority queue: three consecutive runs on an
unchanged tree gave 678/678, 677/1 and 676/2, a different test failing each time.

Serialising the assembly rather than grouping the four into one collection. The
suite takes one to two seconds, so the parallelism was buying nothing, and an
opt-in collection is a rule the next dispatcher-touching test has to remember.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 1: `ShiftService` raises `AccessDenied` on 403

**Files:**
- Modify: `src/VvCash/Services/Api/IShiftService.cs`
- Modify: `src/VvCash/Services/Api/ShiftService.cs`
- Test: `tests/VvCash.Tests/ShiftServiceTest.cs`
- Modify: `tests/VvCash.Tests/PosViewModelSellerGateTest.cs` (the fake's new member — see Step 5)

**Root cause:** `ShiftService` checks only `HttpStatusCode.Unauthorized` (lines 109 and 216).
This backend answers a rejected session with **403**, never 401 on an authenticated route —
`middlewares/utils.go:56` calls `c.AbortWithStatusJSON(http.StatusForbidden, ...)`.
`ExpenseDocumentService.IsSessionRejected` already accepts both; `ShiftService` was missed.
So `SessionRevoked` never fires and `PosViewModel.OnShiftSessionRevoked` is dead code.

403 is **not** merged into the 401 branch. Five different backend paths emit an identical
403 body, and three of them (a tenant-DB pool failure, a missing `is_seller` row, a missing
permission) say nothing about the token — auto-signing-out on those would eject a cashier
over a database blip or trap the register in a login loop.

- [ ] **Step 1: Write the failing tests**

Append to `tests/VvCash.Tests/ShiftServiceTest.cs`, before the closing brace of the class:

```csharp
    // -----------------------------------------------------------------------------
    // 403 — the code this backend actually sends for a rejected session
    // (middlewares/utils.go redirectToAccessDenied). Deliberately a DIFFERENT event
    // from 401: the backend sources of 403 all share one body, and most of them
    // (tenant-DB failure, an inactive tenant, missing is_seller, missing permission)
    // are transient or configuration faults that must never sign a cashier out.
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task GetShiftStateAsync_403_RaisesAccessDenied_NotSessionRevoked_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler(req =>
            (HttpStatusCode.Forbidden, """{"status":"error","message":"forbidden"}"""));
        var svc = CreateService(handler, out _);
        var deniedCount = 0;
        var revokedCount = 0;
        svc.AccessDenied += (s, e) => deniedCount++;
        svc.SessionRevoked += (s, e) => revokedCount++;

        var result = await svc.GetShiftStateAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Null(result);
        Assert.Equal(1, deniedCount);
        Assert.Equal(0, revokedCount);
    }

    [Fact]
    public async Task OpenShiftAsync_403_RaisesAccessDenied_NotSessionRevoked_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler(req =>
            (HttpStatusCode.Forbidden, """{"status":"error","message":"forbidden"}"""));
        var svc = CreateService(handler, out _);
        var deniedCount = 0;
        var revokedCount = 0;
        svc.AccessDenied += (s, e) => deniedCount++;
        svc.SessionRevoked += (s, e) => revokedCount++;

        var result = await svc.OpenShiftAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Null(result);
        Assert.Equal(1, deniedCount);
        Assert.Equal(0, revokedCount);
    }

    [Fact]
    public async Task GetShiftStateAsync_401_DoesNotRaiseAccessDenied()
    {
        // The asymmetry, pinned from the other side: 401 is unambiguous and keeps its
        // own auto-sign-out path. Merging the two events would lose that distinction.
        var handler = new StubHttpMessageHandler(req =>
            (HttpStatusCode.Unauthorized, """{"message":"unauthorized","status":1}"""));
        var svc = CreateService(handler, out _);
        var deniedCount = 0;
        var revokedCount = 0;
        svc.AccessDenied += (s, e) => deniedCount++;
        svc.SessionRevoked += (s, e) => revokedCount++;

        await svc.GetShiftStateAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, deniedCount);
        Assert.Equal(1, revokedCount);
    }

    [Fact]
    public async Task GetShiftStateAsync_NetworkUnreachable_RaisesNeitherEvent()
    {
        // Offline operation is sacred: a request that never reached the server says
        // nothing about the session, so neither event may fire.
        var handler = new StubHttpMessageHandler(req => throw new HttpRequestException("network down"));
        var svc = CreateService(handler, out _);
        var deniedCount = 0;
        var revokedCount = 0;
        svc.AccessDenied += (s, e) => deniedCount++;
        svc.SessionRevoked += (s, e) => revokedCount++;

        var result = await svc.GetShiftStateAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Null(result);
        Assert.Equal(0, deniedCount);
        Assert.Equal(0, revokedCount);
    }

    [Fact]
    public async Task GetShiftStateAsync_Success_RaisesNeitherEvent()
    {
        var handler = new StubHttpMessageHandler(req =>
            (HttpStatusCode.OK, """{"status":0,"body":{"id":"shift-1"}}"""));
        var svc = CreateService(handler, out _);
        var deniedCount = 0;
        var revokedCount = 0;
        svc.AccessDenied += (s, e) => deniedCount++;
        svc.SessionRevoked += (s, e) => revokedCount++;

        var result = await svc.GetShiftStateAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("shift-1", result);
        Assert.Equal(0, deniedCount);
        Assert.Equal(0, revokedCount);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
& ./run-tests.ps1 --filter "FullyQualifiedName~ShiftServiceTest"
```

Expected: compile error — `'IShiftService' does not contain a definition for 'AccessDenied'`.

- [ ] **Step 3: Declare the event on the interface**

In `src/VvCash/Services/Api/IShiftService.cs`, add below the existing `SessionRevoked`
declaration, inside the interface:

```csharp
    /// <summary>Raised when the server answered a shift operation with HTTP 403 — which is
    /// what this backend actually sends for a rejected session (middlewares/utils.go's
    /// redirectToAccessDenied), never 401, on any authenticated route.
    ///
    /// Deliberately separate from <see cref="SessionRevoked"/> rather than folded into it.
    /// Several backend paths produce a byte-identical 403 body — an expired JWT, an invalid
    /// Cash-Authorization token, a tenant-database pool failure, an inactive or deleted
    /// tenant, a missing is_seller row, a denied permission — and only the token ones mean
    /// the session is over. Treating
    /// 403 as a dead token would sign a cashier out over a database blip, or loop them
    /// through the login screen forever when the real fault is a misconfigured permission.
    /// PosViewModel therefore explains it inside the shift modal and leaves the decision to
    /// the cashier, instead of navigating away on its own.
    ///
    /// Never raised for a request that failed to reach the server — see
    /// <see cref="SessionRevoked"/>'s own remarks on why offline must stay silent.</summary>
    event EventHandler? AccessDenied;
```

- [ ] **Step 4: Implement the event and both call sites**

In `src/VvCash/Services/Api/ShiftService.cs`, add next to the existing event field:

```csharp
    public event EventHandler? AccessDenied;
```

Add this method directly below the existing `NotifySessionRevoked`:

```csharp
    /// <summary>Mirrors <see cref="NotifySessionRevoked"/>'s marshalling for the same
    /// reason: the subscriber mutates UI-bound state, and posting keeps this safe if a
    /// future caller ever awaits either method from a background thread.</summary>
    private void NotifyAccessDenied()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            AccessDenied?.Invoke(this, EventArgs.Empty);
        });
    }
```

In `OpenShiftAsync`, immediately after the existing `Unauthorized` block (which ends with
`return null;`), insert:

```csharp
            // The code this backend really uses for a rejected session. Not merged with the
            // 401 branch above: 403 is ambiguous here (see IShiftService.AccessDenied), so
            // it must not reach PosViewModel's automatic sign-out.
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                Console.WriteLine("[ShiftService] OpenShiftAsync got 403 Forbidden — access denied.");
                Debug.WriteLine("[ShiftService] OpenShiftAsync got 403 Forbidden — access denied.");
                NotifyAccessDenied();
                return null;
            }
```

In `GetShiftStateAsync`, immediately after the existing
`else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) { ... }` block,
insert:

```csharp
            else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                // Same ambiguity as OpenShiftAsync's own 403 branch — see
                // IShiftService.AccessDenied for why this is not a sign-out.
                Console.WriteLine("[ShiftService] GetShiftStateAsync got 403 Forbidden — access denied.");
                Debug.WriteLine("[ShiftService] GetShiftStateAsync got 403 Forbidden — access denied.");
                NotifyAccessDenied();
            }
```

- [ ] **Step 5: Teach the test fake the new member, so the tree still builds**

Adding a member to `IShiftService` breaks every implementer of it, including the fake in
the other test file. `--filter` cannot route around this: it selects which tests *run*, not
which files *compile*, so the whole test assembly must build regardless. Leaving that to
the next task would mean shipping a commit that cannot build — useless to `git bisect` and
red in CI on that SHA.

In `tests/VvCash.Tests/PosViewModelSellerGateTest.cs`, inside `private class FakeShiftService`,
directly below the existing `RaiseSessionRevoked` method, add:

```csharp
        public event EventHandler? AccessDenied;

        /// <summary>Stands in for the real ShiftService hitting a 403 — the code this
        /// backend actually sends for a rejected session. Unlike RaiseSessionRevoked this
        /// must NOT lead to a sign-out; see IShiftService.AccessDenied.</summary>
        public void RaiseAccessDenied() => AccessDenied?.Invoke(this, EventArgs.Empty);
```

The raiser goes in now, not just the event: a declared-but-never-raised event warns CS0067,
and Task 2's tests need it anyway.

- [ ] **Step 6: Run the full suite to verify it passes**

```powershell
& ./run-tests.ps1
```

Expected: 684 passed, 0 failed — the 678 baseline plus the six new tests.

- [ ] **Step 7: Commit**

```bash
git add src/VvCash/Services/Api/IShiftService.cs src/VvCash/Services/Api/ShiftService.cs tests/VvCash.Tests/ShiftServiceTest.cs tests/VvCash.Tests/PosViewModelSellerGateTest.cs
git commit -m "fix(shift): recognise the 403 this backend sends for a rejected session

ShiftService checked only 401, which this API emits nowhere except login and
refresh; an authenticated route answers redirectToAccessDenied's 403 instead. So
SessionRevoked never fired and the automatic recovery behind it was dead code.

403 gets its own event rather than joining the 401 branch: the backend paths that
emit it share one body and most of them are transient or configuration faults, so
signing out on it would eject a cashier over a tenant-DB blip.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 2: `PosViewModel` surfaces (and clears) the access-denied state

**Files:**
- Modify: `src/VvCash/ViewModels/PosViewModel.cs`
- Test: `tests/VvCash.Tests/PosViewModelSellerGateTest.cs`

**Why it clears.** `IsSessionRevoked` is deliberately permanent — a dead token does not
heal. `IsShiftAccessDenied` is not: a 403 caused by a tenant-DB pool failure passes on its
own, and leaving a scary red message over a register that has since opened its shift is
worse than useless. It is cleared the instant a shift id actually comes back, from either
path, via the generated `OnCurrentShiftIdChanged` hook.

`FakeShiftService` already carries `AccessDenied` and `RaiseAccessDenied` — Task 1 added them
so that its own commit would still build. This task consumes them.

- [ ] **Step 1: Write the failing tests**

In the same file, append inside the class, directly after
`SignOutCommand_WorksWithNoOpenShift_RegardlessOfWhyTheModalIsUp`:

```csharp
    // ---------------------------------------------------------------------------------
    // Access denied (403): the code this backend really sends for a rejected session.
    // Unlike the 401 path above, this must NOT sign the cashier out — most of the things
    // that make this backend answer 403 are transient or configuration faults. It raises an
    // explanation inside the shift modal instead, and unlike IsSessionRevoked it clears
    // itself once a shift id actually comes back.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ShiftServiceAccessDenied_SetsIsShiftAccessDenied_OnUiThread_WithoutSigningOut()
    {
        using var vm = CreateViewModel(out var deps, d => d.ShiftService.GetShiftStateResult = null);
        var logoutCount = 0;
        vm.LogoutRequested += (s, e) => logoutCount++;
        Assert.False(vm.IsShiftAccessDenied);

        deps.ShiftService.RaiseAccessDenied();

        // Not yet: the handler posts rather than mutating inline, same marshalling proof
        // as the 401 tests above.
        Assert.False(vm.IsShiftAccessDenied);

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsShiftAccessDenied);
        Assert.Equal(0, logoutCount);
        Assert.Equal(0, deps.AuthService.ClearSessionCallCount);
    }

    [Fact]
    public async Task IsShiftAccessDenied_ClearsOnceAShiftIdComesBack()
    {
        // A 403 from a tenant-database blip passes on its own. Leaving the warning up over
        // a register that has since opened its shift would be a lie.
        using var vm = CreateViewModel(out var deps, d => d.ShiftService.GetShiftStateResult = null);
        deps.ShiftService.RaiseAccessDenied();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsShiftAccessDenied);

        deps.ShiftService.OpenShiftResult = "shift-9";
        await vm.OpenShiftCommand.ExecuteAsync(null);

        Assert.Equal("shift-9", vm.CurrentShiftId);
        Assert.False(vm.IsShiftAccessDenied);
    }

    [Fact]
    public void Dispose_UnsubscribesFromShiftServiceAccessDenied()
    {
        var vm = CreateViewModel(out var deps, d => d.ShiftService.GetShiftStateResult = null);
        vm.Dispose();

        deps.ShiftService.RaiseAccessDenied();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsShiftAccessDenied);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
& ./run-tests.ps1 --filter "FullyQualifiedName~PosViewModelSellerGateTest"
```

Expected: compile error — `'PosViewModel' does not contain a definition for 'IsShiftAccessDenied'`.

- [ ] **Step 3: Add the property, the clear hook and the handler**

In `src/VvCash/ViewModels/PosViewModel.cs`, directly below the existing
`[ObservableProperty] private bool _isSessionRevoked;` declaration, add:

```csharp
    /// <summary>Set when ShiftService reports HTTP 403 on a shift operation — the code this
    /// backend actually sends for a rejected session.
    ///
    /// Unlike <see cref="IsSessionRevoked"/> this is NOT permanent, and the difference is
    /// the whole point. A 401 means the token is dead and a dead token does not heal. A 403
    /// here is ambiguous: an expired JWT, a bad cash token, a missing seller right and a
    /// tenant-database pool failure all produce the identical body, and the last of those
    /// passes on its own. So this clears the moment a shift id actually comes back — see
    /// <see cref="OnCurrentShiftIdChanged"/> — rather than leaving a red warning over a
    /// register that has since opened its shift.
    ///
    /// Drives the explanation inside the shift modal rather than the top banner:
    /// PosView.axaml's Start Shift Modal Overlay is Grid.RowSpan="3" at ZIndex 1000 and
    /// covers that banner completely.</summary>
    [ObservableProperty] private bool _isShiftAccessDenied;

    /// <summary>A shift id in hand proves the server accepted this session after all, so a
    /// 403 raised earlier must stop showing. Hooked here rather than at the two assignment
    /// sites (InitializeAsync's GetShiftStateAsync and the OpenShift command) so neither can
    /// be added to later without the reset coming along. DoCloseShiftAsync assigns null on a
    /// successful close, which correctly does not clear anything.</summary>
    partial void OnCurrentShiftIdChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value)) IsShiftAccessDenied = false;
    }
```

Directly below the existing `OnShiftSessionRevoked` method, add:

```csharp
    /// <summary>Reaction to a 403 on a shift operation. Deliberately does everything
    /// <see cref="OnShiftSessionRevoked"/> does not: no sign-out, no navigation, no touching
    /// of credentials. Three of the five things that make this backend answer 403 say
    /// nothing about the session (see IShiftService.AccessDenied), so the register explains
    /// itself and leaves the decision to the cashier, who already has a sign-out button on
    /// the very modal this message appears in. Marshals to the UI thread for the same reason
    /// every other handler here does — ShiftService posts the event rather than invoking it
    /// inline.</summary>
    private void OnShiftAccessDenied(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsShiftAccessDenied = true);
    }
```

In `InitializeAsync`, directly below the existing line
`_shiftService.SessionRevoked += OnShiftSessionRevoked;`, add:

```csharp
        _shiftService.AccessDenied += OnShiftAccessDenied;
```

In `Dispose`, directly below the existing line
`_shiftService.SessionRevoked -= OnShiftSessionRevoked;`, add:

```csharp
        _shiftService.AccessDenied -= OnShiftAccessDenied;
```

- [ ] **Step 4: Run the tests to verify they pass**

```powershell
& ./run-tests.ps1
```

Expected: 687 passed, 0 failed. Task 0 serialised the suite, so a failure here is a real
one — read it rather than re-running until it goes away.

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/ViewModels/PosViewModel.cs tests/VvCash.Tests/PosViewModelSellerGateTest.cs
git commit -m "fix(pos): explain a 403 on the shift instead of leaving a mute dead end

A rejected shift session now raises IsShiftAccessDenied rather than signing the
cashier out, because the 403 behind it is ambiguous. The flag clears as soon as a
shift id comes back, which is what separates it from IsSessionRevoked: a dead
token does not heal, a tenant-database blip does.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 3: The shift modal says what happened

**Files:**
- Modify: `src/VvCash/Views/PosView.axaml:1007-1012`
- Modify: `src/VvCash/Assets/i18n/ru.json`, `en.json`, `tg.json`, `uz.json`, `kk.json`

**No unit test.** Avalonia bindings in this project are reflective, so a wrong path
compiles clean. This task is verified by Task 9's manual pass.

- [ ] **Step 1: Add the i18n key to all five locales**

A missing key renders on screen as `[ShiftAccessDenied]`, so all five must be done
together. Insert before the final `}` of each file (add a comma to the previous last line).

`src/VvCash/Assets/i18n/ru.json`:

```json
  "ShiftAccessDenied": "Сервер отклонил сессию этой кассы — смену открыть не получится. Выйдите и войдите заново. Если не помогло, проверьте токен кассы и права продавца."
```

`src/VvCash/Assets/i18n/en.json`:

```json
  "ShiftAccessDenied": "The server rejected this register's session, so the shift cannot be opened. Sign out and sign in again. If that does not help, check the cash token and the seller's permissions."
```

`src/VvCash/Assets/i18n/tg.json`:

```json
  "ShiftAccessDenied": "Сервер сессияи ин кассаро рад кард — сменаро кушода намешавад. Бароед ва аз нав ворид шавед. Агар кӯмак накунад, токени касса ва ҳуқуқи фурӯшандаро санҷед."
```

`src/VvCash/Assets/i18n/uz.json`:

```json
  "ShiftAccessDenied": "Server ushbu kassa seansini rad etdi — smenani ochib bo'lmaydi. Chiqing va qaytadan kiring. Yordam bermasa, kassa tokeni va sotuvchi huquqlarini tekshiring."
```

`src/VvCash/Assets/i18n/kk.json`:

```json
  "ShiftAccessDenied": "Сервер осы касса сеансын қабылдамады — ауысымды ашу мүмкін емес. Шығып, қайта кіріңіз. Көмектеспесе, касса токенін және сатушы құқықтарын тексеріңіз."
```

- [ ] **Step 2: Swap the modal's explanation line**

In `src/VvCash/Views/PosView.axaml`, replace this block (currently at lines 1007-1012):

```xml
                    <TextBlock Text="{Binding [Pleasestartyourshift], Source={x:Static services:I18nService.Instance}}"
                             FontSize="14"
                             Foreground="{StaticResource Slate600Brush}"
                             TextWrapping="Wrap"
                             TextAlignment="Center"/>
```

with:

```xml
                    <!-- Two mutually exclusive lines. The ordinary invitation, and — when
                         the server answered a shift operation with 403 — an explanation of
                         why the button below cannot work and what to do about it. This has
                         to live inside the modal: the IsSessionRevoked banner at the top of
                         this screen is left dimmed under this overlay's translucent scrim
                         (RowSpan 3, ZIndex 1000), and a dimmed line behind a modal that is
                         demanding a decision is easy to miss entirely. -->
                    <TextBlock Text="{Binding [Pleasestartyourshift], Source={x:Static services:I18nService.Instance}}"
                             FontSize="14"
                             Foreground="{StaticResource Slate600Brush}"
                             TextWrapping="Wrap"
                             TextAlignment="Center"
                             IsVisible="{Binding !IsShiftAccessDenied}"/>

                    <TextBlock Text="{Binding [ShiftAccessDenied], Source={x:Static services:I18nService.Instance}}"
                             FontSize="14"
                             FontWeight="SemiBold"
                             Foreground="{StaticResource DangerBrush}"
                             TextWrapping="Wrap"
                             TextAlignment="Center"
                             IsVisible="{Binding IsShiftAccessDenied}"/>
```

- [ ] **Step 3: Verify every locale still parses and carries the key**

```powershell
foreach ($l in 'ru','en','tg','uz','kk') {
  $j = Get-Content "src/VvCash/Assets/i18n/$l.json" -Raw | ConvertFrom-Json
  "$l : " + $(if ($j.ShiftAccessDenied) { 'ok' } else { 'MISSING' })
}
```

Expected: five lines, all `ok`. A parse error here means a missing or doubled comma.

- [ ] **Step 4: Build to confirm the XAML compiles**

```powershell
dotnet build src/VvCash/VvCash.csproj -o build/verify
```

Expected: `Build succeeded`.

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Views/PosView.axaml src/VvCash/Assets/i18n
git commit -m "fix(pos): tell the cashier why the shift will not open

The shift modal covers the session banner completely, so a register refused with
403 showed a mute Start Shift button and nothing else. The explanation goes inside
the modal, next to the sign-out button that is the actual way out.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 4: `CustomerDisplayPlacementSelector`

**Files:**
- Create: `src/VvCash/Services/CustomerDisplayPlacementSelector.cs`
- Test: `tests/VvCash.Tests/CustomerDisplayPlacementSelectorTest.cs` (create)

**Why this exists.** Two jobs in one pure function: decide whether a customer-display
window should exist at all and where it goes, and give a single-monitor development
machine a way to exercise that path — otherwise the whole of Tasks 5 and 6 would be
unverifiable. Modelled on `src/VvCash/Services/Rendering/RenderingSelector.cs`, which
solves the same "environment variable plus facts about the machine, decided before any UI
exists" problem and is tested the same way.

- [ ] **Step 1: Write the failing tests**

Create `tests/VvCash.Tests/CustomerDisplayPlacementSelectorTest.cs`:

```csharp
using System;
using System.Collections.Generic;
using Avalonia;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class CustomerDisplayPlacementSelectorTest
{
    private static IReadOnlyList<PixelRect> Screens(params PixelRect[] screens) => screens;

    private static readonly PixelRect Primary = new(0, 0, 1920, 1080);
    private static readonly PixelRect Secondary = new(1920, 0, 1280, 1024);

    [Fact]
    public void SingleScreenWithNoOverride_MeansNoWindow()
    {
        // Today's production behaviour, pinned: a register with one monitor has nowhere to
        // put a customer-facing window, so none is built.
        Assert.Null(CustomerDisplayPlacementSelector.Select(null, Screens(Primary)));
    }

    [Fact]
    public void TwoScreens_PlacesItOnTheSecond_AndIsNotForced()
    {
        var placement = CustomerDisplayPlacementSelector.Select(null, Screens(Primary, Secondary));

        Assert.NotNull(placement);
        Assert.Equal(Secondary.Position, placement!.Position);
        Assert.False(placement.ForcedOnSingleScreen);
    }

    [Fact]
    public void ForceOnASingleScreen_PlacesItOnTheOnlyScreen_AndMarksItForced()
    {
        // The development escape hatch. ForcedOnSingleScreen is what makes the host raise the
        // window above the full-screen Topmost MainWindow — without it the window would be
        // created, shown, and completely invisible.
        var placement = CustomerDisplayPlacementSelector.Select("force", Screens(Primary));

        Assert.NotNull(placement);
        Assert.Equal(Primary.Position, placement!.Position);
        Assert.True(placement.ForcedOnSingleScreen);
    }

    [Fact]
    public void ForceOnTwoScreens_BehavesExactlyLikeAutomatic()
    {
        // The variable forces the window to EXIST, not to be a debugging overlay. On a real
        // two-screen register it already lands on its own screen, and making it Topmost over
        // the POS would be a regression.
        var placement = CustomerDisplayPlacementSelector.Select("force", Screens(Primary, Secondary));

        Assert.NotNull(placement);
        Assert.Equal(Secondary.Position, placement!.Position);
        Assert.False(placement.ForcedOnSingleScreen);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("  Off  ")]
    public void Off_SuppressesTheWindowEvenOnATwoScreenRegister(string value)
    {
        // For silencing a real customer display while debugging on the shop floor. Case and
        // surrounding whitespace are tolerated, same as RenderingSelector's override.
        Assert.Null(CustomerDisplayPlacementSelector.Select(value, Screens(Primary, Secondary)));
    }

    [Fact]
    public void AnUnrecognisedValueFallsThroughToAutomatic()
    {
        // This runs before any window exists, so a typo must not throw — there would be no
        // UI in which to report it. Mirrors RenderingSelector's own tolerance.
        Assert.Null(CustomerDisplayPlacementSelector.Select("yes-please", Screens(Primary)));

        var placement = CustomerDisplayPlacementSelector.Select("yes-please", Screens(Primary, Secondary));
        Assert.NotNull(placement);
        Assert.Equal(Secondary.Position, placement!.Position);
    }

    [Fact]
    public void NoScreensAtAll_MeansNoWindow()
    {
        // Not reachable on a live system, but Select must still answer rather than index
        // off the end of an empty list before any UI exists to report the crash.
        Assert.Null(CustomerDisplayPlacementSelector.Select(null, Screens()));
        Assert.Null(CustomerDisplayPlacementSelector.Select("force", Screens()));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
& ./run-tests.ps1 --filter "FullyQualifiedName~CustomerDisplayPlacementSelectorTest"
```

Expected: compile error — `The name 'CustomerDisplayPlacementSelector' does not exist`.

- [ ] **Step 3: Write the implementation**

Create `src/VvCash/Services/CustomerDisplayPlacementSelector.cs`:

```csharp
using System;
using System.Collections.Generic;
using Avalonia;

namespace VvCash.Services;

/// <summary>Where the customer-facing window goes, or that it should not exist.</summary>
/// <param name="Position">Top-left corner, in device pixels.</param>
/// <param name="ForcedOnSingleScreen">True only when the window was forced onto a machine that
/// has just one screen. The host reads this to make the window Topmost and modestly sized:
/// MainWindow is full-screen and Topmost, so a customer window merely placed beside it on
/// the same monitor would sit behind it and be invisible. Always false in production.</param>
public sealed record CustomerDisplayPlacement(PixelPoint Position, bool ForcedOnSingleScreen);

/// <summary>Decides whether this register gets a customer-facing window and where it lands,
/// before any such window exists.
///
/// Split out as a pure function for the same reason as
/// <see cref="VvCash.Services.Rendering.RenderingSelector"/>: the decision depends on the
/// machine (how many screens) and on an environment variable, neither of which a test can
/// arrange through a running Avalonia application. Without the override below, the whole
/// customer-display path is unreachable on a single-monitor development machine.</summary>
public static class CustomerDisplayPlacementSelector
{
    /// <summary>Set to <c>force</c> to get the window on a machine with one screen, or to
    /// <c>off</c> to suppress it on a register that really has two. Anything else — including
    /// a typo — falls through to the automatic decision rather than throwing: this runs
    /// before the first window exists, so there would be nowhere to report the error.</summary>
    public const string OverrideVariable = "VVCASH_CUSTOMER_DISPLAY";

    /// <summary>Returns the placement, or <c>null</c> when no window should be created.</summary>
    public static CustomerDisplayPlacement? Select(string? overrideValue, IReadOnlyList<PixelRect> screens)
    {
        var mode = overrideValue?.Trim().ToLowerInvariant();

        if (mode == "off") return null;
        if (screens.Count == 0) return null;

        // A real second screen always wins, override or not. "force" exists to make the
        // window EXIST on a one-screen machine, not to turn a genuine customer display into
        // a Topmost overlay on top of the POS.
        if (screens.Count > 1)
            return new CustomerDisplayPlacement(screens[1].Position, ForcedOnSingleScreen: false);

        return mode == "force"
            ? new CustomerDisplayPlacement(screens[0].Position, ForcedOnSingleScreen: true)
            : null;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```powershell
& ./run-tests.ps1
```

Expected: **699 passed, 0 failed** — 687 plus this task's 12 cases (each of the two
theories contributes three).

> **Review outcome:** the code review held this against its stated model,
> `RenderingSelector`/`RenderingSelectorTest`, and required three additions before it was
> honestly at parity: a three-screen case (so narrowing `> 1` to `== 2` cannot pass), the
> `""` and `"   "` override cases the model pins explicitly, and dropping the `screens is
> null` guard — the parameter is non-nullable under `<Nullable>enable</Nullable>` and the
> model trusts its own contract rather than defending an unreachable state. `ForcedForTesting`
> was also renamed to `ForcedOnSingleScreen`: production host code branches on it, so a name
> implying a test-only concern misleads at the call site. The code above already reflects all
> of that.

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Services/CustomerDisplayPlacementSelector.cs tests/VvCash.Tests/CustomerDisplayPlacementSelectorTest.cs
git commit -m "feat(display): decide the customer window's placement in one pure function

Also the only way the customer-display path can be exercised on a single-monitor
development machine, which the next two tasks depend on. Shaped after
RenderingSelector, including its refusal to throw on a bad override value.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 5: `PosViewModel` announces customer-display visibility

**Files:**
- Modify: `src/VvCash/ViewModels/PosViewModel.cs`
- Test: `tests/VvCash.Tests/PosViewModelSellerGateTest.cs`

**Why an event.** `App.axaml.cs` owns the window; `PosViewModel` owns the feature flag.
Same split as `LogoutRequested` and `ShutdownRequested`: the view model states intent, the
host performs the window mechanics.

- [ ] **Step 1: Write the failing test**

In `tests/VvCash.Tests/PosViewModelSellerGateTest.cs`, append inside the class, after the
access-denied tests added in Task 2:

```csharp
    [Fact]
    public void CustomerDisplayVisibilityChanged_FiresWithEachNewFlagValue()
    {
        // The host owns the window, this class owns the flag. The event is how the two meet
        // — see App.axaml.cs's NavigateToPos, which also applies the CURRENT value on
        // subscribe, because this only fires on a change.
        using var vm = CreateViewModel(out _);
        var seen = new List<bool>();
        vm.CustomerDisplayVisibilityChanged += (s, visible) => seen.Add(visible);

        vm.IsCustomerDisplayEnabled = false;
        vm.IsCustomerDisplayEnabled = true;

        Assert.Equal(new[] { false, true }, seen);
    }

    [Fact]
    public void CustomerDisplayDisabled_StillParksTheDisplayViewModelAsIdle()
    {
        // The pre-existing behaviour of the same hook must survive the event being added to
        // it: a display already fed cart data before the flag loaded must not keep showing
        // someone else's total.
        using var vm = CreateViewModel(out _);
        vm.CustomerDisplayViewModel = new CustomerDisplayViewModel { IsIdle = false };

        vm.IsCustomerDisplayEnabled = false;

        Assert.True(vm.CustomerDisplayViewModel.IsIdle);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

```powershell
& ./run-tests.ps1 --filter "FullyQualifiedName~PosViewModelSellerGateTest"
```

Expected: compile error — `'PosViewModel' does not contain a definition for 'CustomerDisplayVisibilityChanged'`.

- [ ] **Step 3: Add the event and raise it**

In `src/VvCash/ViewModels/PosViewModel.cs`, directly above the existing
`partial void OnIsCustomerDisplayEnabledChanged(bool value)` method, add:

```csharp
    /// <summary>Raised whenever <see cref="IsCustomerDisplayEnabled"/> changes, so the host
    /// (App.axaml.cs) can show or hide the customer-facing window it owns. Same decoupling
    /// role as <see cref="LogoutRequested"/>: this class states intent, the host performs
    /// the window mechanics.
    ///
    /// A subscriber must ALSO apply the current value of the flag when it subscribes. This
    /// only fires on a change, and ICashFeatureService is a singleton that survives a
    /// logout/login cycle — so by the time the host wires this up the flag may already hold
    /// its final value and never raise anything at all.</summary>
    public event EventHandler<bool>? CustomerDisplayVisibilityChanged;
```

Then replace the body of `OnIsCustomerDisplayEnabledChanged` with:

```csharp
    partial void OnIsCustomerDisplayEnabledChanged(bool value)
    {
        if (!value && CustomerDisplayViewModel != null)
            CustomerDisplayViewModel.IsIdle = true;

        CustomerDisplayVisibilityChanged?.Invoke(this, value);
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

```powershell
& ./run-tests.ps1
```

Expected: 702 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/ViewModels/PosViewModel.cs tests/VvCash.Tests/PosViewModelSellerGateTest.cs
git commit -m "feat(pos): announce customer-display visibility to the host

The feature flag lives here, the window lives in App.axaml.cs. Same split as
LogoutRequested: state the intent, let the host do the window mechanics.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 6: One customer-display window for the whole run

**Files:**
- Modify: `src/VvCash/App.axaml.cs`

**Root cause:** `NavigateToPos` built `new CustomerDisplayWindow(...).Show()` on every call,
kept no reference and closed nothing, so each logout/login cycle stacked another window on
the customer's screen. It also never consulted `CashFeatureCodes.CustomerDisplay` — the flag
only gated pushing cart data, so a store that switched the function off still got a window
saying "Welcome!".

**No unit test.** `App.axaml.cs` is not reachable from this test project — see the header of
`PosViewModelSellerGateTest`, which states the same for the rest of this file's wiring.
Verified by Task 9.

- [ ] **Step 1: Add the two missing usings**

In `src/VvCash/App.axaml.cs`, add to the top of the using block:

```csharp
using System.Collections.Generic;
```

`using Avalonia;` (for `PixelRect`/`PixelPoint`) and `using VvCash.Services;` (for the
selector) are already present.

- [ ] **Step 2: Declare the window alongside the other per-run state**

Directly below the existing `SellerSwitchViewModel? activeSellerSwitchVm = null;`
declaration, add:

```csharp
            // One window for the whole run. It is NOT created here: Screens.All only reports
            // the real layout once MainWindow has actually opened — which is exactly why the
            // remembered-session path below defers NavigateToPos to MainWindow.Opened — so
            // the first NavigateToPos is the earliest moment this can be decided. Built on
            // demand there, reused afterwards, never rebuilt: it used to be constructed
            // fresh on every navigation with nothing holding the previous one, so a
            // logout->login cycle left another window on the customer's screen every time.
            CustomerDisplayWindow? customerWindow = null;
```

- [ ] **Step 3: Hide the window when the cashier signs out**

Replace the existing `posVm.LogoutRequested` handler body so it reads:

```csharp
                posVm.LogoutRequested += (s, explanation) =>
                {
                    // The customer's screen must not keep showing the finished cart while
                    // the next cashier types their password.
                    customerWindow?.Hide();
                    loginVm.ErrorMessage = explanation;
                    mainVm.NavigateTo(loginVm);
                };
```

- [ ] **Step 4: Replace the window-construction block**

In `NavigateToPos`, replace this block entirely:

```csharp
                var screens = desktop.MainWindow?.Screens.All;
                if (screens != null && screens.Count > 1)
                {
                    var secondScreen = screens[1];
                    var customerVm = Services.GetRequiredService<CustomerDisplayViewModel>();
                    posVm.CustomerDisplayViewModel = customerVm;
                    posVm.NavigationRequest = mainVm.NavigateTo;

                    var customerWindow = new CustomerDisplayWindow
                    {
                        DataContext = customerVm,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Position = new PixelPoint(secondScreen.Bounds.X, secondScreen.Bounds.Y)
                    };
                    customerWindow.Show();
                }
```

with:

```csharp
                // No LINQ: this file does not use it, and one loop is clearer than a cast
                // dance around a possibly-null Screens.All.
                var screenBounds = new List<PixelRect>();
                var allScreens = desktop.MainWindow?.Screens.All;
                if (allScreens != null)
                {
                    foreach (var screen in allScreens) screenBounds.Add(screen.Bounds);
                }

                var placement = CustomerDisplayPlacementSelector.Select(
                    Environment.GetEnvironmentVariable(CustomerDisplayPlacementSelector.OverrideVariable),
                    screenBounds);

                if (placement != null)
                {
                    if (customerWindow == null)
                    {
                        customerWindow = new CustomerDisplayWindow
                        {
                            WindowStartupLocation = WindowStartupLocation.Manual,
                            Position = placement.Position,
                        };

                        // Single-monitor debugging only. MainWindow is full-screen and
                        // Topmost, so a customer window merely placed beside it on the same
                        // screen would sit behind it and never be seen. Never true in
                        // production — see CustomerDisplayPlacementSelector.
                        if (placement.ForcedOnSingleScreen)
                        {
                            customerWindow.Topmost = true;
                            customerWindow.Width = 640;
                            customerWindow.Height = 400;
                        }
                    }

                    // The window survives; only what it shows is replaced. CustomerDisplayViewModel
                    // is transient, like PosViewModel itself, so each navigation brings a fresh one.
                    var customerVm = Services.GetRequiredService<CustomerDisplayViewModel>();
                    posVm.CustomerDisplayViewModel = customerVm;
                    customerWindow.DataContext = customerVm;

                    // SubscribeCustomerDisplayVisibility, not +=. The generated
                    // OnIsCustomerDisplayEnabledChanged fires only on a CHANGE, and
                    // ICashFeatureService is a singleton that survives logout->login — so the
                    // flag may already hold its final value by now and never raise anything at
                    // all. That method subscribes AND calls the handler with the current value,
                    // so the initial sync cannot be forgotten here.
                    var window = customerWindow;
                    posVm.SubscribeCustomerDisplayVisibility((s, visible) =>
                    {
                        if (visible) window.Show(); else window.Hide();
                    });
                }
```

Note the removed `posVm.NavigationRequest = mainVm.NavigateTo;` line: it was a duplicate of
the identical assignment made earlier in `NavigateToPos`, and it only ever ran on
multi-screen registers.

- [ ] **Step 5: Build and run the whole suite**

```powershell
dotnet build src/VvCash/VvCash.csproj -o build/verify
& ./run-tests.ps1
```

Expected: `Build succeeded`, then 702 passed, 0 failed.

- [ ] **Step 6: Commit**

```bash
git add src/VvCash/App.axaml.cs
git commit -m "fix(display): keep one customer window per run and obey the feature flag

NavigateToPos built a new CustomerDisplayWindow every time it ran and held no
reference to the previous one, so each logout/login cycle left another window on
the customer's screen. It also never read cash_customer_display_enabled, which
only gated pushing cart data -- a store that switched the display off still got a
window. The window is now built once and reused, its DataContext swapped per
navigation, its visibility driven by the flag, and it hides on sign-out.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 7: Remove the unsynced-documents wipe

**Files:**
- Modify: `src/VvCash/Services/Data/IOfflineStorageService.cs:57`
- Modify: `src/VvCash/Services/Data/OfflineStorageService.cs` (the `ClearUnsyncedDocumentsAsync` method)
- Modify: `src/VvCash/ViewModels/SettingsViewModel.cs` (the `ClearUnsyncedDocuments` command)
- Modify: `src/VvCash/Views/SettingsView.axaml` (the "Clear Unsynced Documents" block)
- Modify: `tests/VvCash.Tests/CashFeatureServiceTest.cs`, `ExpenseDocumentServiceTest.cs`, `PosViewModelSellerGateTest.cs`, `SellerRosterServiceTest.cs`, `SettingsViewModelTest.cs`, `SyncServiceTest.cs`

**Why removal rather than a confirmation.** The settings screen opens from the login screen,
before any authentication, and this button permanently destroyed sales the register had
already taken money for. The problem it existed to solve — a queue that could never drain —
was fixed properly by `MarkDocumentRejectedAsync`, which takes a document the server refuses
on its merits out of the retry rotation while keeping the row on disk for the back office.
What is left is only a way to lose revenue in one tap.

- [ ] **Step 1: Remove the method from the interface**

In `src/VvCash/Services/Data/IOfflineStorageService.cs`, delete this line:

```csharp
    Task ClearUnsyncedDocumentsAsync();
```

- [ ] **Step 2: Remove the implementation**

In `src/VvCash/Services/Data/OfflineStorageService.cs`, delete this whole method:

```csharp
    public async Task ClearUnsyncedDocumentsAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM UnsyncedDocuments";
        await command.ExecuteNonQueryAsync();
    }
```

- [ ] **Step 3: Remove the command**

In `src/VvCash/ViewModels/SettingsViewModel.cs`, delete:

```csharp
    [RelayCommand]
    private async Task ClearUnsyncedDocuments()
    {
        await _offlineStorageService.ClearUnsyncedDocumentsAsync();
    }
```

- [ ] **Step 4: Remove the button**

In `src/VvCash/Views/SettingsView.axaml`, delete this entire block:

```xml
                        <!-- Clear Unsynced Documents (unsold products) -->
                        <Border Background="{StaticResource Slate50Brush}" BorderBrush="{StaticResource Slate200Brush}" BorderThickness="1" CornerRadius="10" Padding="16,12">
                            <Grid ColumnDefinitions="*, Auto">
                                <StackPanel Grid.Column="0" VerticalAlignment="Center" Spacing="2">
                                    <TextBlock Text="{Binding [ClearUnsyncedDocs], Source={x:Static services:I18nService.Instance}}" FontSize="15" FontWeight="SemiBold" Foreground="{StaticResource Slate800Brush}"/>
                                    <TextBlock Text="{Binding [ClearUnsyncedDocsDesc], Source={x:Static services:I18nService.Instance}}" FontSize="12" Foreground="{StaticResource Slate500Brush}"/>
                                </StackPanel>
                                <Button Grid.Column="1" Classes="DangerButton" Command="{Binding ClearUnsyncedDocumentsCommand}" HorizontalAlignment="Right">
                                    <StackPanel Orientation="Horizontal" Spacing="8">
                                        <material:MaterialIcon Kind="TrashCan" Width="18" Height="18"/>
                                        <TextBlock Text="{Binding [ClearUnsyncedDocs], Source={x:Static services:I18nService.Instance}}" VerticalAlignment="Center"/>
                                    </StackPanel>
                                </Button>
                            </Grid>
                        </Border>
```

- [ ] **Step 5: Remove the line from all six test fakes**

Delete this exact line from each of `tests/VvCash.Tests/CashFeatureServiceTest.cs`,
`ExpenseDocumentServiceTest.cs`, `PosViewModelSellerGateTest.cs`,
`SellerRosterServiceTest.cs`, `SettingsViewModelTest.cs`, `SyncServiceTest.cs`:

```csharp
        public Task ClearUnsyncedDocumentsAsync() => Task.CompletedTask;
```

- [ ] **Step 6: Retire the now-dead i18n keys**

First confirm nothing else references them:

```powershell
Select-String -Path src/VvCash -Pattern 'ClearUnsyncedDocs' -Recurse
```

Expected: matches only inside `src/VvCash/Assets/i18n/*.json`. If any `.axaml` or `.cs`
still matches, stop and remove that reference first. Then delete the
`"ClearUnsyncedDocs"` and `"ClearUnsyncedDocsDesc"` entries from all five locale files
(`ru`, `en`, `tg`, `uz`, `kk`), minding the trailing commas.

- [ ] **Step 7: Verify nothing references the removed method**

```powershell
Select-String -Path src,tests -Pattern 'ClearUnsyncedDocuments' -Recurse
```

Expected: no matches at all.

- [ ] **Step 8: Build and run the whole suite**

```powershell
& ./run-tests.ps1
```

Expected: 702 passed, 0 failed.

- [ ] **Step 9: Commit**

```bash
git add src tests
git commit -m "fix(settings): drop the button that wiped the unsynced-sales queue

One tap, from a screen reachable before anyone has authenticated, permanently
destroyed sales the register had already taken money for. The stuck-queue problem
it was added for is handled properly by MarkDocumentRejectedAsync, which retires a
refused document from the retry rotation while keeping the row for the back
office, so all that was left here was a way to lose revenue.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 8: Confirmation before the remaining catalog wipes

**Files:**
- Modify: `src/VvCash/ViewModels/SettingsViewModel.cs`
- Modify: `src/VvCash/Views/SettingsView.axaml`
- Modify: `src/VvCash/Assets/i18n/ru.json`, `en.json`, `tg.json`, `uz.json`, `kk.json`
- Test: `tests/VvCash.Tests/SettingsViewModelTest.cs`

`ClearProducts` and `ClearCategories` are recoverable through a sync, but on an offline
register they remove the ability to sell anything until connectivity returns. The overlay
copies the shape of `PosViewModel`'s existing `IsShiftCloseConfirmVisible` confirm rather
than inventing a second one.

- [ ] **Step 1: Give the test fake call counters**

In `tests/VvCash.Tests/SettingsViewModelTest.cs`, inside `private sealed class FakeStorage`,
replace these two lines:

```csharp
        public Task ClearCategoriesAsync() => Task.CompletedTask;
        public Task ClearProductsAsync() => Task.CompletedTask;
```

with:

```csharp
        public int ClearCategoriesCallCount { get; private set; }
        public int ClearProductsCallCount { get; private set; }
        public Task ClearCategoriesAsync() { ClearCategoriesCallCount++; return Task.CompletedTask; }
        public Task ClearProductsAsync() { ClearProductsCallCount++; return Task.CompletedTask; }
```

Then replace the `Build` helper with an overload pair so existing tests keep compiling
unchanged:

```csharp
    private static SettingsViewModel Build(out FakeSettings settings)
        => Build(out settings, out _);

    private static SettingsViewModel Build(out FakeSettings settings, out FakeStorage storage)
    {
        settings = new FakeSettings();
        storage = new FakeStorage();
        return new SettingsViewModel(
            new MainViewModel(),
            settings,
            storage,
            new FakeFeatures(),
            new FakePaymentCategories());
    }
```

- [ ] **Step 2: Write the failing tests**

Append inside the class in `tests/VvCash.Tests/SettingsViewModelTest.cs`:

```csharp
    // -----------------------------------------------------------------------------
    // The two remaining destructive buttons. This screen opens from the login screen,
    // before anyone has authenticated, and on an offline register a wiped catalog means
    // nothing can be sold until connectivity returns — so neither button may act on a
    // single tap.
    // -----------------------------------------------------------------------------

    [Fact]
    public void ClearProducts_OnlyArmsTheConfirmation_AndTouchesNothing()
    {
        var vm = Build(out _, out var storage);

        vm.ClearProductsCommand.Execute(null);

        Assert.True(vm.IsConfirmVisible);
        Assert.False(string.IsNullOrWhiteSpace(vm.ConfirmMessage));
        Assert.Equal(0, storage.ClearProductsCallCount);
    }

    [Fact]
    public void ClearCategories_OnlyArmsTheConfirmation_AndTouchesNothing()
    {
        var vm = Build(out _, out var storage);

        vm.ClearCategoriesCommand.Execute(null);

        Assert.True(vm.IsConfirmVisible);
        Assert.Equal(0, storage.ClearCategoriesCallCount);
    }

    [Fact]
    public async Task Confirm_RunsTheArmedActionAndClosesTheOverlay()
    {
        var vm = Build(out _, out var storage);
        vm.ClearProductsCommand.Execute(null);

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(1, storage.ClearProductsCallCount);
        Assert.Equal(0, storage.ClearCategoriesCallCount);
        Assert.False(vm.IsConfirmVisible);
    }

    [Fact]
    public async Task CancelConfirm_LeavesStorageAlone_AndDisarmsTheAction()
    {
        // Disarming matters as much as closing: a Confirm arriving later — a stray second
        // tap, a keyboard Enter — must not run an action the operator already refused.
        var vm = Build(out _, out var storage);
        vm.ClearCategoriesCommand.Execute(null);

        vm.CancelConfirmCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(0, storage.ClearCategoriesCallCount);
        Assert.False(vm.IsConfirmVisible);
    }

    [Fact]
    public async Task ArmingASecondActionReplacesTheFirst()
    {
        var vm = Build(out _, out var storage);
        vm.ClearProductsCommand.Execute(null);
        vm.ClearCategoriesCommand.Execute(null);

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(0, storage.ClearProductsCallCount);
        Assert.Equal(1, storage.ClearCategoriesCallCount);
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

```powershell
& ./run-tests.ps1 --filter "FullyQualifiedName~SettingsViewModelTest"
```

Expected: compile error — `'SettingsViewModel' does not contain a definition for 'IsConfirmVisible'`.

- [ ] **Step 4: Add the i18n keys to all five locales**

`src/VvCash/Assets/i18n/ru.json`:

```json
  "ConfirmClearProducts": "Удалить весь каталог товаров с этой кассы? Пока не пройдёт следующая синхронизация, продавать будет нечего.",
  "ConfirmClearCategories": "Удалить все категории с этой кассы? Пока не пройдёт следующая синхронизация, каталог будет пустым.",
  "ConfirmDelete": "Удалить"
```

`src/VvCash/Assets/i18n/en.json`:

```json
  "ConfirmClearProducts": "Delete the entire product catalog from this register? Until the next sync completes there will be nothing to sell.",
  "ConfirmClearCategories": "Delete all categories from this register? Until the next sync completes the catalog will be empty.",
  "ConfirmDelete": "Delete"
```

`src/VvCash/Assets/i18n/tg.json`:

```json
  "ConfirmClearProducts": "Тамоми феҳристи молҳо аз ин хазина нест карда шавад? То синхронизатсияи навбатӣ чизе барои фурӯхтан намемонад.",
  "ConfirmClearCategories": "Ҳамаи гурӯҳҳо аз ин хазина нест карда шаванд? То синхронизатсияи навбатӣ феҳрист холӣ мемонад.",
  "ConfirmDelete": "Нест кардан"
```

`src/VvCash/Assets/i18n/uz.json`:

```json
  "ConfirmClearProducts": "Ushbu kassadan butun mahsulot katalogi o'chirilsinmi? Keyingi sinxronizatsiyagacha sotadigan narsa qolmaydi.",
  "ConfirmClearCategories": "Ushbu kassadan barcha toifalar o'chirilsinmi? Keyingi sinxronizatsiyagacha katalog bo'sh bo'ladi.",
  "ConfirmDelete": "O'chirish"
```

`src/VvCash/Assets/i18n/kk.json`:

```json
  "ConfirmClearProducts": "Осы кассадан бүкіл тауар каталогы жойылсын ба? Келесі синхрондауға дейін сататын ештеңе болмайды.",
  "ConfirmClearCategories": "Осы кассадан барлық санаттар жойылсын ба? Келесі синхрондауға дейін каталог бос болады.",
  "ConfirmDelete": "Жою"
```

- [ ] **Step 5: Add the overlay state and rewrite the two commands**

In `src/VvCash/ViewModels/SettingsViewModel.cs`, replace both existing commands:

```csharp
    [RelayCommand]
    private async Task ClearCategories()
    {
        await _offlineStorageService.ClearCategoriesAsync();
        await _offlineStorageService.SetLastSyncVersionAsync(0);
    }

    [RelayCommand]
    private async Task ClearProducts()
    {
        await _offlineStorageService.ClearProductsAsync();
        await _offlineStorageService.SetLastSyncVersionAsync(0);
    }
```

with:

```csharp
    /// <summary>Confirmation overlay state. This screen is reachable from the login screen,
    /// before anyone has authenticated, and on an offline register a wiped catalog means
    /// nothing can be sold until connectivity returns — so the two destructive buttons no
    /// longer do the work themselves. They arm <see cref="_pendingAction"/> and raise this.
    /// Shaped after PosViewModel's own IsShiftCloseConfirmVisible overlay rather than
    /// inventing a second confirmation pattern.</summary>
    [ObservableProperty] private bool _isConfirmVisible;

    [ObservableProperty] private string _confirmMessage = string.Empty;

    /// <summary>What <see cref="ConfirmCommand"/> will run. Cleared by both exits, so a
    /// stray second tap after a cancel cannot run an action the operator already refused.</summary>
    private Func<Task>? _pendingAction;

    private void AskToConfirm(string message, Func<Task> action)
    {
        _pendingAction = action;
        ConfirmMessage = message;
        IsConfirmVisible = true;
    }

    [RelayCommand]
    private async Task Confirm()
    {
        // Taken and cleared before it runs, so a slow action cannot be started twice.
        var action = _pendingAction;
        _pendingAction = null;
        IsConfirmVisible = false;
        if (action != null) await action();
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        _pendingAction = null;
        IsConfirmVisible = false;
    }

    [RelayCommand]
    private void ClearCategories()
        => AskToConfirm(I18nService.Instance["ConfirmClearCategories"], async () =>
        {
            await _offlineStorageService.ClearCategoriesAsync();
            await _offlineStorageService.SetLastSyncVersionAsync(0);
        });

    [RelayCommand]
    private void ClearProducts()
        => AskToConfirm(I18nService.Instance["ConfirmClearProducts"], async () =>
        {
            await _offlineStorageService.ClearProductsAsync();
            await _offlineStorageService.SetLastSyncVersionAsync(0);
        });
```

- [ ] **Step 6: Run the tests to verify they pass**

```powershell
& ./run-tests.ps1 --filter "FullyQualifiedName~SettingsViewModelTest"
```

Expected: PASS.

- [ ] **Step 7: Add the overlay to the view**

In `src/VvCash/Views/SettingsView.axaml`, inside the root
`<Grid RowDefinitions="Auto, *" ...>` (line 119), immediately before its closing `</Grid>`,
add:

```xml
        <!-- Confirmation for the two destructive actions above. The buttons no longer act
             on a single tap: they arm the action and raise this. Mirrors PosView's own
             shift-close confirm overlay. -->
        <Border Grid.Row="0" Grid.RowSpan="2"
                Background="#80000000"
                IsVisible="{Binding IsConfirmVisible}"
                ZIndex="1000">
            <Border Background="White"
                    CornerRadius="16"
                    Padding="32"
                    Width="460"
                    HorizontalAlignment="Center"
                    VerticalAlignment="Center">
                <StackPanel Spacing="24">
                    <material:MaterialIcon Kind="AlertCircleOutline" Width="56" Height="56"
                                           Foreground="{StaticResource DangerBrush}"
                                           HorizontalAlignment="Center"/>
                    <TextBlock Text="{Binding ConfirmMessage}"
                               FontSize="15"
                               Foreground="{StaticResource Slate700Brush}"
                               TextWrapping="Wrap"
                               TextAlignment="Center"/>
                    <StackPanel Orientation="Horizontal" Spacing="12" HorizontalAlignment="Center">
                        <Button Classes="DangerButton"
                                Command="{Binding ConfirmCommand}"
                                Content="{Binding [ConfirmDelete], Source={x:Static services:I18nService.Instance}}"
                                Width="190" Height="48" HorizontalContentAlignment="Center"/>
                        <Button Classes="BackButton"
                                Command="{Binding CancelConfirmCommand}"
                                Content="{Binding [Cancel], Source={x:Static services:I18nService.Instance}}"
                                Width="190" Height="48" HorizontalContentAlignment="Center"/>
                    </StackPanel>
                </StackPanel>
            </Border>
        </Border>
```

- [ ] **Step 8: Verify the locales parse and build**

```powershell
foreach ($l in 'ru','en','tg','uz','kk') {
  $j = Get-Content "src/VvCash/Assets/i18n/$l.json" -Raw | ConvertFrom-Json
  "$l : " + $(if ($j.ConfirmClearProducts -and $j.ConfirmClearCategories -and $j.ConfirmDelete -and $j.Cancel) { 'ok' } else { 'MISSING' })
}
dotnet build src/VvCash/VvCash.csproj -o build/verify
& ./run-tests.ps1
```

Expected: five `ok` lines, `Build succeeded`, then 707 passed, 0 failed.

- [ ] **Step 9: Commit**

```bash
git add src tests
git commit -m "fix(settings): confirm before wiping the catalog

Clearing products or categories is recoverable through a sync, but on an offline
register it removes the ability to sell anything until connectivity returns, and
this screen is reachable before anyone has authenticated. Both buttons now arm a
confirmation instead of acting on a single tap.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 9: Manual verification pass

**Files:** none modified.

Everything below is unreachable from the test project — `App.axaml.cs` wiring and XAML
bindings, which are reflective in this project and so fail silently at runtime rather than
at compile time.

- [ ] **Step 1: Confirm the suite matches the expected end state**

```powershell
& ./run-tests.ps1
```

Expected: 707 passed, 0 failed. Task 0 removed the dispatcher race, so any failure here is
a real one. Run it twice: a result that differs between runs means something reintroduced
parallel access to `Dispatcher.UIThread`.

- [ ] **Step 2: Customer display, forced onto a single monitor**

```powershell
$env:VVCASH_CUSTOMER_DISPLAY = 'force'
dotnet run --project src/VvCash/VvCash.csproj
```

Check, in order:
1. A small customer-display window appears on top of the POS after login.
2. Adding a product to the cart updates that window's total.
3. Sign out through the exit menu's "hand the register over" — the customer window **hides**, and the login screen appears.
4. Log in again — exactly **one** customer window is on screen, not two.
5. Close the app.

- [ ] **Step 3: Customer display, feature flag off**

With `cash_customer_display_enabled` switched off for this register on the backend, launch
again with `VVCASH_CUSTOMER_DISPLAY=force`. The customer window must **not** be visible
once `InitializeAsync` has refreshed the flags (a brief appearance before the first refresh
is expected and correct — the flag defaults to enabled until the cached map is read).

- [ ] **Step 4: Customer display, override off**

```powershell
$env:VVCASH_CUSTOMER_DISPLAY = 'off'
dotnet run --project src/VvCash/VvCash.csproj
```

No customer window at all. Then clear the variable
(`Remove-Item Env:\VVCASH_CUSTOMER_DISPLAY`) and confirm a single-monitor run also shows
none — that is production behaviour on a one-screen register.

- [ ] **Step 5: The 403 explanation in the shift modal**

Point the register at a backend that will answer 403 — simplest is to set a syntactically
valid but unknown `Cash-Authorization` token in Settings, which makes
`getCashFromToken` fail with `errInvalidCashToken`. Launch and confirm:
1. The shift modal shows the red `ShiftAccessDenied` explanation instead of "Please start your shift".
2. Pressing Start Shift leaves the explanation up rather than clearing it.
3. The register does **not** navigate to the login screen on its own.
4. The sign-out button on that modal works and returns to login.
5. Restore a valid token, sign in, open a shift — the explanation is gone.

- [ ] **Step 6: Settings confirmations**

From the login screen, open Settings and confirm:
1. There is no "Clear unsynced documents" block at all.
2. "Clear products" raises the overlay and does nothing on its own.
3. Cancel closes the overlay; the catalog is still there after going back to the POS.
4. "Clear categories" → confirm → the categories really are gone.
5. Switch the language to `en` and reopen — the overlay text is English, not `[ConfirmClearProducts]`.

- [ ] **Step 7: Record the outcome**

If every check passes, note it in the PR/commit description. If any check fails, stop and
fix before considering batch A done — do not open batch B on top of an unverified batch A.

---

## Done when

- All ten tasks (0 through 9) are checked off.
- `& ./run-tests.ps1` reports 707 passed and 0 failed, twice in a row.
- Task 9's manual checklist passed on a real run of the app.
- Findings 1, 2 and 5 from the code review are closed. Findings 3, 4, 6–18 remain open and
  belong to batches B, C and D.
