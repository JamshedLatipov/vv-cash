using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Api;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

public class SellerSwitchViewModelTest
{
    // Same low-iteration PBKDF2 approach as SellerSessionTest.Encode — only
    // speed matters for these fixtures, not cross-language byte agreement.
    private static string Encode(string pin)
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        using var kdf = new Rfc2898DeriveBytes(pin, salt, 1000, HashAlgorithmName.SHA256);
        return $"pbkdf2_sha256$1000${Convert.ToBase64String(salt)}${Convert.ToBase64String(kdf.GetBytes(32))}";
    }

    private static async Task<SellerSession> SessionWithRoster()
    {
        var session = new SellerSession(() => new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc), TimeSpan.FromSeconds(90));
        await session.LoadRosterAsync(new List<SellerInfo>
        {
            new() { Id = "u-1", FirstName = "Азиз", PinHash = Encode("4821"), CanSell = true },
            new() { Id = "u-2", FirstName = "Дилноза", PinHash = Encode("9073"), CanSell = true, CanCloseShift = true }
        });
        return session;
    }

    [Fact]
    public async Task SelectSeller_MovesToPinEntry()
    {
        var vm = new SellerSwitchViewModel(await SessionWithRoster(), new FakeSellerRosterService());
        vm.Open();

        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        Assert.True(vm.IsPinEntry);
        Assert.Equal("Азиз", vm.SelectedSeller?.FirstName);
    }

    [Fact]
    public async Task AppendDigit_BuildsPinAndAutoSubmitsAtFourDigits()
    {
        var session = await SessionWithRoster();
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        foreach (var d in "4821")
            await vm.AppendDigitCommand.ExecuteAsync(d.ToString());

        Assert.Equal("u-1", session.Current?.Id);
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public async Task WrongPin_ShowsErrorAndClearsInput_AndStaysOpen()
    {
        var session = await SessionWithRoster();
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        foreach (var d in "0000")
            await vm.AppendDigitCommand.ExecuteAsync(d.ToString());

        Assert.True(vm.HasError);
        Assert.Equal(string.Empty, vm.Pin);
        Assert.True(vm.IsVisible);
        Assert.Null(session.Current);
        Assert.Equal(I18nService.Instance["SellerPinWrong"], vm.ErrorMessage);
    }

    [Fact]
    public async Task Backspace_RemovesLastDigit()
    {
        var vm = new SellerSwitchViewModel(await SessionWithRoster(), new FakeSellerRosterService());
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        await vm.AppendDigitCommand.ExecuteAsync("4");
        await vm.AppendDigitCommand.ExecuteAsync("8");
        vm.BackspaceCommand.Execute(null);

        Assert.Equal("4", vm.Pin);
    }

    [Fact]
    public async Task OpenForApproval_ListsOnlySellersWithTheRight()
    {
        var vm = new SellerSwitchViewModel(await SessionWithRoster(), new FakeSellerRosterService());

        vm.OpenForApproval(s => s.CanCloseShift);

        Assert.Single(vm.Sellers);
        Assert.Equal("u-2", vm.Sellers[0].Id);
    }

    [Fact]
    public async Task IsApprovalMode_ReflectsWhichOpenMethodWasCalled()
    {
        var vm = new SellerSwitchViewModel(await SessionWithRoster(), new FakeSellerRosterService());

        vm.Open();
        Assert.False(vm.IsApprovalMode);

        vm.OpenForApproval(s => s.CanCloseShift);
        Assert.True(vm.IsApprovalMode);

        // A later plain Open() (e.g. an ordinary seller switch) must clear it again —
        // this is what drives the view back to the "Who is selling?" heading.
        vm.Open();
        Assert.False(vm.IsApprovalMode);
    }

    [Fact]
    public async Task Approval_RaisesApproved_AndDoesNotChangeCurrentSeller()
    {
        var session = await SessionWithRoster();
        await session.SwitchAsync("u-1", "4821");
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());

        SellerInfo? approver = null;
        vm.Approved += (_, s) => approver = s;
        vm.OpenForApproval(s => s.CanCloseShift);
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        foreach (var d in "9073")
            await vm.AppendDigitCommand.ExecuteAsync(d.ToString());

        Assert.Equal("u-2", approver?.Id);
        Assert.Equal("u-1", session.Current?.Id);
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public async Task Approval_AgainstLockedSupervisor_ShowsLockedMessage_NotWrongPin()
    {
        var session = await SessionWithRoster();
        // Lock u-2 out via five wrong attempts (SwitchAsync and ApproveAsync share
        // one lockout counter per seller — see SellerSession's remarks).
        for (var i = 0; i < 5; i++)
            await session.SwitchAsync("u-2", "0000");

        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());
        SellerInfo? approver = null;
        vm.Approved += (_, s) => approver = s;
        vm.OpenForApproval(s => s.CanCloseShift);
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        // Correct PIN, but u-2 is locked — no PIN, not even the right one, is
        // checked while locked.
        foreach (var d in "9073")
            await vm.AppendDigitCommand.ExecuteAsync(d.ToString());

        Assert.True(vm.HasError);
        Assert.True(vm.IsVisible);
        Assert.Null(approver);
        Assert.Equal(I18nService.Instance["SellerLocked"], vm.ErrorMessage);
        Assert.NotEqual(I18nService.Instance["SellerPinWrong"], vm.ErrorMessage);
    }

    [Fact]
    public async Task Approval_AgainstCorruptHash_ShowsHashCorruptMessage_NotWrongPin()
    {
        var session = new SellerSession(() => new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc), TimeSpan.FromSeconds(90));
        await session.LoadRosterAsync(new List<SellerInfo>
        {
            new() { Id = "u-4", FirstName = "Битый", PinHash = "not-a-valid-hash", CanSell = true, CanCloseShift = true }
        });
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());
        SellerInfo? approver = null;
        vm.Approved += (_, s) => approver = s;
        vm.OpenForApproval(s => s.CanCloseShift);
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        foreach (var d in "4821")
            await vm.AppendDigitCommand.ExecuteAsync(d.ToString());

        Assert.True(vm.HasError);
        Assert.True(vm.IsVisible);
        Assert.Null(approver);
        Assert.Equal(I18nService.Instance["SellerHashCorrupt"], vm.ErrorMessage);
        Assert.NotEqual(I18nService.Instance["SellerPinWrong"], vm.ErrorMessage);
    }

    [Fact]
    public async Task CorruptHash_ProducesItsOwnMessage_DistinctFromWrongPin()
    {
        var session = new SellerSession(() => new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc), TimeSpan.FromSeconds(90));
        await session.LoadRosterAsync(new List<SellerInfo>
        {
            new() { Id = "u-4", FirstName = "Битый", PinHash = "not-a-valid-hash", CanSell = true }
        });
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        foreach (var d in "4821")
            await vm.AppendDigitCommand.ExecuteAsync(d.ToString());

        Assert.True(vm.HasError);
        Assert.True(vm.IsVisible);
        Assert.Equal(I18nService.Instance["SellerHashCorrupt"], vm.ErrorMessage);
        Assert.NotEqual(I18nService.Instance["SellerPinWrong"], vm.ErrorMessage);
    }

    [Fact]
    public async Task Open_AfterAPreviousFailedAttempt_ClearsErrorAndPin()
    {
        var session = await SessionWithRoster();
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);
        foreach (var d in "0000")
            await vm.AppendDigitCommand.ExecuteAsync(d.ToString());
        Assert.True(vm.HasError); // sanity check on the premise

        vm.Open();

        Assert.False(vm.HasError);
        Assert.Equal(string.Empty, vm.ErrorMessage);
        Assert.Equal(string.Empty, vm.Pin);
        Assert.Null(vm.SelectedSeller);
        Assert.False(vm.IsPinEntry);
    }

    [Fact]
    public async Task AppendDigit_CalledAgainAfterPinIsFull_IsIgnoredByLengthGuard()
    {
        // NOT a concurrency test: SellerSession's own Task-returning members are
        // backed by Task.FromResult/Task.CompletedTask (see its class remarks) —
        // no real suspension point — so both ExecuteAsync calls below run to
        // completion sequentially, one fully finishing before the next starts.
        // What this actually proves is that AppendDigitAsync's own
        // `Pin.Length >= PinLength` guard rejects a call that lands once the PIN
        // is already full (e.g. a stray extra tap after auto-submit already
        // fired) — not that it survives a genuine race. See
        // WhileSubmitIsPending_OtherEntryPointsAreIgnored below for the version
        // that exercises a real suspension point via a controllable fake session.
        var session = await SessionWithRoster();
        var raised = 0;
        session.CurrentChanged += (_, _) => raised++;
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        await vm.AppendDigitCommand.ExecuteAsync("4");
        await vm.AppendDigitCommand.ExecuteAsync("8");
        await vm.AppendDigitCommand.ExecuteAsync("2");

        var first = vm.AppendDigitCommand.ExecuteAsync("1");
        var second = vm.AppendDigitCommand.ExecuteAsync("1");
        await Task.WhenAll(first, second);

        Assert.Equal(1, raised);
        Assert.Equal("u-1", session.Current?.Id);
    }

    [Fact]
    public async Task WhileSubmitIsPending_OtherEntryPointsAreIgnored()
    {
        // Exercises the _isBusy guard for real: unlike SellerSession, SlowSession
        // below does not complete SwitchAsync synchronously, so awaiting it is a
        // genuine suspension point — the same kind a future roster refresh or
        // server round-trip inside SellerSession would introduce (see Task 19).
        // While that await is pending, SelectSeller/Back/Open must all be no-ops;
        // without _isBusy each would mutate state out from under the still-
        // pending SubmitAsync continuation.
        var roster = new List<SellerInfo>
        {
            new() { Id = "u-1", FirstName = "Азиз", CanSell = true },
            new() { Id = "u-2", FirstName = "Дилноза", CanSell = true }
        };
        var session = new SlowSession(roster);
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        await vm.AppendDigitCommand.ExecuteAsync("1");
        await vm.AppendDigitCommand.ExecuteAsync("2");
        await vm.AppendDigitCommand.ExecuteAsync("3");
        // The fourth digit starts SubmitAsync, which suspends on SlowSession's
        // controllable task — the overlay is now genuinely "mid-submit"
        // (_isBusy == true).
        var submitting = vm.AppendDigitCommand.ExecuteAsync("4");

        vm.SelectSellerCommand.Execute(vm.Sellers[1]);
        Assert.Equal("u-1", vm.SelectedSeller?.Id);

        vm.BackCommand.Execute(null);
        Assert.True(vm.IsPinEntry);

        vm.Open();
        Assert.Equal("u-1", vm.SelectedSeller?.Id);
        Assert.True(vm.IsPinEntry);

        session.CompleteSwitch(SwitchResult.Ok, vm.Sellers[0]);
        await submitting;

        Assert.False(vm.IsVisible);
    }

    /// <summary>Minimal ISellerSession fake whose SwitchAsync returns a task held
    /// open by a TaskCompletionSource, so a test can pause mid-submit — something
    /// SellerSession itself can't do today, since its async members always
    /// complete synchronously (see its class remarks). ApproveAsync isn't
    /// exercised by the busy-gate test above and just fails.</summary>
    private sealed class SlowSession : ISellerSession
    {
        private readonly TaskCompletionSource<SwitchResult> _pendingSwitch = new();

        public SlowSession(IReadOnlyList<SellerInfo> roster) => Roster = roster;

        public SellerInfo? Current { get; private set; }
        public bool IsStale => false;
        public IReadOnlyList<SellerInfo> Roster { get; }
        public event EventHandler? CurrentChanged;

        public Task LoadRosterAsync(IEnumerable<SellerInfo> sellers) => Task.CompletedTask;

        public Task<SwitchResult> SwitchAsync(string sellerId, string pin) => _pendingSwitch.Task;

        public Task<ApprovalResult> ApproveAsync(string sellerId, string pin)
            => Task.FromResult(ApprovalResult.Failure(SwitchResult.WrongPin));

        public void Touch() { }

        public void Clear() { }

        public void CompleteSwitch(SwitchResult result, SellerInfo? seller = null)
        {
            if (result == SwitchResult.Ok && seller != null)
            {
                Current = seller;
                CurrentChanged?.Invoke(this, EventArgs.Empty);
            }

            _pendingSwitch.SetResult(result);
        }
    }

    // ---------------------------------------------------------------------------------
    // PIN setup (Task 19): a seller whose PinHash is empty gets to create their own
    // PIN instead of hitting "PIN не задан". These fakes stand in for
    // ISellerRosterService — SetPinAsync is genuine network I/O in production, so
    // SlowRosterService below (mirroring SlowSession above) is what proves the
    // _isBusy gate actually covers it.
    // ---------------------------------------------------------------------------------

    private sealed class FakeSellerRosterService : ISellerRosterService
    {
        public bool SetPinResult { get; set; } = true;
        public List<SellerInfo> CachedRoster { get; set; } = new();
        public int SetPinCallCount { get; private set; }
        public string? LastSetPinSellerId { get; private set; }
        public string? LastSetPinValue { get; private set; }

        public Task<IEnumerable<SellerInfo>> RefreshAsync() => Task.FromResult<IEnumerable<SellerInfo>>(CachedRoster);
        public Task<IEnumerable<SellerInfo>> GetCachedAsync() => Task.FromResult<IEnumerable<SellerInfo>>(CachedRoster);

        public Task<bool> SetPinAsync(string sellerId, string pin)
        {
            SetPinCallCount++;
            LastSetPinSellerId = sellerId;
            LastSetPinValue = pin;
            return Task.FromResult(SetPinResult);
        }
    }

    /// <summary>Unlike FakeSellerRosterService, SetPinAsync here genuinely suspends
    /// until the test calls CompleteSetPin — the only way to make the PIN-setup
    /// flow's network round-trip actually overlap with a second entry point, the
    /// same idea as SlowSession above.</summary>
    private sealed class SlowRosterService : ISellerRosterService
    {
        private readonly TaskCompletionSource<bool> _pendingSetPin = new();
        public List<SellerInfo> CachedRoster { get; set; } = new();

        public Task<IEnumerable<SellerInfo>> RefreshAsync() => Task.FromResult<IEnumerable<SellerInfo>>(CachedRoster);
        public Task<IEnumerable<SellerInfo>> GetCachedAsync() => Task.FromResult<IEnumerable<SellerInfo>>(CachedRoster);
        public Task<bool> SetPinAsync(string sellerId, string pin) => _pendingSetPin.Task;

        public void CompleteSetPin(bool result) => _pendingSetPin.SetResult(result);
    }

    private static async Task<SellerSession> SessionWithNoPinSeller()
    {
        var session = new SellerSession(() => new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc), TimeSpan.FromSeconds(90));
        await session.LoadRosterAsync(new List<SellerInfo>
        {
            new() { Id = "u-5", FirstName = "Жасур", PinHash = "", CanSell = true }
        });
        return session;
    }

    [Fact]
    public async Task PinNotSet_SwitchMode_EntersPinCreationFlow_NotAnError()
    {
        var session = await SessionWithNoPinSeller();
        var roster = new FakeSellerRosterService();
        var vm = new SellerSwitchViewModel(session, roster);
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        foreach (var d in "1357")
            await vm.AppendDigitCommand.ExecuteAsync(d.ToString());

        Assert.True(vm.IsSettingPin);
        Assert.True(vm.IsCreatingNewPin);
        Assert.False(vm.IsRepeatingNewPin);
        Assert.False(vm.HasError);
        Assert.True(vm.IsVisible); // still open, mid-setup — not an error dead-end
        Assert.Equal(string.Empty, vm.Pin); // cleared, ready for the confirm prompt
        Assert.Equal(0, roster.SetPinCallCount); // no network call from the first entry alone
    }

    [Theory]
    [InlineData("1111")]
    [InlineData("0000")]
    [InlineData("1234")]
    [InlineData("4321")]
    [InlineData("9876")]
    public async Task PinCreation_TrivialFirstEntry_RejectedLocally_NoNetworkCall(string trivialPin)
    {
        var session = await SessionWithNoPinSeller();
        var roster = new FakeSellerRosterService();
        var vm = new SellerSwitchViewModel(session, roster);
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);
        foreach (var d in "1357") await vm.AppendDigitCommand.ExecuteAsync(d.ToString()); // enter PIN setup

        foreach (var d in trivialPin)
            await vm.AppendDigitCommand.ExecuteAsync(d.ToString());

        Assert.True(vm.HasError);
        Assert.Equal(I18nService.Instance["PinTooWeak"], vm.ErrorMessage);
        Assert.False(vm.IsConfirmingNewPin); // still on the first entry, not advanced
        Assert.True(vm.IsSettingPin);
        Assert.Equal(string.Empty, vm.Pin);
        Assert.Equal(0, roster.SetPinCallCount);
    }

    [Fact]
    public async Task PinCreation_MismatchedConfirmation_ShowsMismatchAndRestartsFromTheFirstStep()
    {
        var session = await SessionWithNoPinSeller();
        var roster = new FakeSellerRosterService();
        var vm = new SellerSwitchViewModel(session, roster);
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        foreach (var d in "0000") await vm.AppendDigitCommand.ExecuteAsync(d.ToString()); // any PIN fails as PinNotSet -> BeginPinSetup
        Assert.True(vm.IsCreatingNewPin); // sanity check on the premise
        foreach (var d in "1357") await vm.AppendDigitCommand.ExecuteAsync(d.ToString()); // first ("create") entry
        Assert.True(vm.IsConfirmingNewPin); // sanity check on the premise

        foreach (var d in "9999") await vm.AppendDigitCommand.ExecuteAsync(d.ToString());

        Assert.True(vm.HasError);
        Assert.Equal(I18nService.Instance["PinMismatch"], vm.ErrorMessage);
        Assert.False(vm.IsConfirmingNewPin); // restarted to the first prompt, no stale first entry
        Assert.True(vm.IsCreatingNewPin);
        Assert.Equal(string.Empty, vm.Pin);
        Assert.True(vm.IsSettingPin); // still in the setup flow, not aborted
        Assert.True(vm.IsVisible);
        Assert.Equal(0, roster.SetPinCallCount); // never reached the network call
    }

    [Fact]
    public async Task PinCreation_RestartAfterMismatch_ThenMatchingConfirmation_Succeeds()
    {
        // Proves the restart genuinely discards the first attempt rather than
        // half-remembering it: after a mismatch, a *fresh* matching pair must still
        // work end-to-end.
        var session = await SessionWithNoPinSeller();
        var roster = new FakeSellerRosterService
        {
            CachedRoster = new List<SellerInfo>
            {
                new() { Id = "u-5", FirstName = "Жасур", PinHash = Encode("2468"), CanSell = true }
            }
        };
        var vm = new SellerSwitchViewModel(session, roster);
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);
        foreach (var d in "0000") await vm.AppendDigitCommand.ExecuteAsync(d.ToString()); // triggers BeginPinSetup
        foreach (var d in "1357") await vm.AppendDigitCommand.ExecuteAsync(d.ToString()); // first "create" entry
        foreach (var d in "9999") await vm.AppendDigitCommand.ExecuteAsync(d.ToString()); // mismatch, restarts
        Assert.True(vm.HasError);

        foreach (var d in "2468") await vm.AppendDigitCommand.ExecuteAsync(d.ToString()); // fresh "create" entry
        foreach (var d in "2468") await vm.AppendDigitCommand.ExecuteAsync(d.ToString()); // matching confirm

        Assert.Equal(1, roster.SetPinCallCount); // exactly one SetPinAsync call, for the fresh pair
        Assert.Equal("2468", roster.LastSetPinValue);
        Assert.Equal("u-5", session.Current?.Id);
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public async Task PinCreation_MatchingConfirmation_CallsSetPinAsync_ReloadsRoster_AndSelectsSeller()
    {
        var session = await SessionWithNoPinSeller();
        var roster = new FakeSellerRosterService
        {
            // After SetPinAsync succeeds, the view model must reload the roster from
            // the roster service's cache — this is what makes the freshly-set PIN
            // actually verify against the real SellerSession afterwards.
            CachedRoster = new List<SellerInfo>
            {
                new() { Id = "u-5", FirstName = "Жасур", PinHash = Encode("1357"), CanSell = true }
            }
        };
        var vm = new SellerSwitchViewModel(session, roster);
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        foreach (var d in "0000") await vm.AppendDigitCommand.ExecuteAsync(d.ToString()); // triggers BeginPinSetup
        foreach (var d in "1357") await vm.AppendDigitCommand.ExecuteAsync(d.ToString()); // "create" entry
        foreach (var d in "1357") await vm.AppendDigitCommand.ExecuteAsync(d.ToString()); // matching confirm

        Assert.Equal(1, roster.SetPinCallCount);
        Assert.Equal("u-5", roster.LastSetPinSellerId);
        Assert.Equal("1357", roster.LastSetPinValue);
        Assert.Equal("u-5", session.Current?.Id); // the seller ends up selected
        Assert.False(vm.IsVisible);
        Assert.False(vm.IsSettingPin);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task PinCreation_OfflineSetPinFailure_ShowsOfflineMessage_ClosesWithoutSelectingAnyone()
    {
        var session = await SessionWithNoPinSeller();
        var roster = new FakeSellerRosterService { SetPinResult = false };
        var vm = new SellerSwitchViewModel(session, roster);
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        foreach (var d in "0000") await vm.AppendDigitCommand.ExecuteAsync(d.ToString()); // triggers BeginPinSetup
        foreach (var d in "1357") await vm.AppendDigitCommand.ExecuteAsync(d.ToString()); // "create" entry
        foreach (var d in "1357") await vm.AppendDigitCommand.ExecuteAsync(d.ToString()); // matching confirm

        Assert.False(vm.IsVisible); // closes ...
        Assert.Null(session.Current); // ... without changing the seller (falls back to the shift owner)
        Assert.Equal(I18nService.Instance["SellerPinSetupOffline"], vm.ErrorMessage);
    }

    [Fact]
    public async Task PinCreation_WhileSetPinAsyncIsPending_OtherEntryPointsAreIgnored()
    {
        // Exercises the _isBusy guard around the genuine network suspension point:
        // SetPinAsync (unlike SellerSession's own members) is real I/O in
        // production, and SlowRosterService is what lets a test actually pause
        // mid-request instead of racing a call that resolves synchronously.
        var session = await SessionWithNoPinSeller();
        var roster = new SlowRosterService
        {
            CachedRoster = new List<SellerInfo>
            {
                new() { Id = "u-5", FirstName = "Жасур", PinHash = Encode("1357"), CanSell = true }
            }
        };
        var vm = new SellerSwitchViewModel(session, roster);
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);
        foreach (var d in "0000") await vm.AppendDigitCommand.ExecuteAsync(d.ToString()); // triggers BeginPinSetup
        foreach (var d in "1357") await vm.AppendDigitCommand.ExecuteAsync(d.ToString()); // "create" entry
        foreach (var d in "135") await vm.AppendDigitCommand.ExecuteAsync(d.ToString()); // confirm entry, 3 of 4 digits

        // The fourth digit of the confirm entry starts the SetPinAsync round-trip,
        // which suspends on SlowRosterService's controllable task — the overlay is
        // now genuinely mid-request (_isBusy == true).
        var submitting = vm.AppendDigitCommand.ExecuteAsync("7");

        vm.BackCommand.Execute(null);
        Assert.True(vm.IsPinEntry); // Back is a no-op while busy
        Assert.True(vm.IsSettingPin);

        vm.Open();
        Assert.True(vm.IsSettingPin); // Open() is also a no-op while busy

        roster.CompleteSetPin(true);
        await submitting;

        Assert.False(vm.IsVisible);
        Assert.Equal("u-5", session.Current?.Id);
    }
}
