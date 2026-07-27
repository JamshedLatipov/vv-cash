using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services;
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
        var vm = new SellerSwitchViewModel(await SessionWithRoster());
        vm.Open();

        vm.SelectSellerCommand.Execute(vm.Sellers[0]);

        Assert.True(vm.IsPinEntry);
        Assert.Equal("Азиз", vm.SelectedSeller?.FirstName);
    }

    [Fact]
    public async Task AppendDigit_BuildsPinAndAutoSubmitsAtFourDigits()
    {
        var session = await SessionWithRoster();
        var vm = new SellerSwitchViewModel(session);
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
        var vm = new SellerSwitchViewModel(session);
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
        var vm = new SellerSwitchViewModel(await SessionWithRoster());
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
        var vm = new SellerSwitchViewModel(await SessionWithRoster());

        vm.OpenForApproval(s => s.CanCloseShift);

        Assert.Single(vm.Sellers);
        Assert.Equal("u-2", vm.Sellers[0].Id);
    }

    [Fact]
    public async Task IsApprovalMode_ReflectsWhichOpenMethodWasCalled()
    {
        var vm = new SellerSwitchViewModel(await SessionWithRoster());

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
        var vm = new SellerSwitchViewModel(session);

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

        var vm = new SellerSwitchViewModel(session);
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
        var vm = new SellerSwitchViewModel(session);
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
        var vm = new SellerSwitchViewModel(session);
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
        var vm = new SellerSwitchViewModel(session);
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
        var vm = new SellerSwitchViewModel(session);
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
        var vm = new SellerSwitchViewModel(session);
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
}
