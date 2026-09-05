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

    // The overlay opening with an unsatisfiable filter is the real-world case behind
    // this: opening returns on a register where nobody holds the refund permission
    // (CanRefund comes from the backend's documents.MakeReturn grant). Before the
    // notice, that rendered a heading and a close button over an empty white card.
    [Fact]
    public async Task OpenForApproval_WithNobodyHoldingTheRight_FlagsTheMissingApprover()
    {
        var vm = new SellerSwitchViewModel(await SessionWithRoster(), new FakeSellerRosterService());

        vm.OpenForApproval(s => s.CanRefund);

        Assert.Empty(vm.Sellers);
        Assert.True(vm.HasNoApprover);
        // The switch-mode notice must stay off — an empty *filter result* is not an
        // empty roster, and the two point at different remedies.
        Assert.False(vm.HasEmptyRoster);
        Assert.False(vm.IsPinEntry);
        Assert.True(vm.IsVisible);
    }

    [Fact]
    public async Task OpenForApproval_WithAnApproverAvailable_ShowsNoNotice()
    {
        var vm = new SellerSwitchViewModel(await SessionWithRoster(), new FakeSellerRosterService());

        vm.OpenForApproval(s => s.CanCloseShift);

        Assert.False(vm.HasNoApprover);
        Assert.False(vm.HasEmptyRoster);
    }

    [Fact]
    public async Task Open_WithEmptyRoster_FlagsTheEmptyRoster()
    {
        var session = new SellerSession(() => new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc), TimeSpan.FromSeconds(90));
        await session.LoadRosterAsync(new List<SellerInfo>());
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());

        vm.Open();

        Assert.True(vm.HasEmptyRoster);
        Assert.False(vm.HasNoApprover);
    }

    // Reopening in the other mode must flip which notice is showing: the collection is
    // empty in both of these, so only IsApprovalMode distinguishes them.
    [Fact]
    public async Task EmptyStateNotices_FollowTheModeTheOverlayReopensIn()
    {
        var session = new SellerSession(() => new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc), TimeSpan.FromSeconds(90));
        await session.LoadRosterAsync(new List<SellerInfo>());
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());

        vm.Open();
        Assert.True(vm.HasEmptyRoster);

        vm.OpenForApproval(s => s.CanRefund);
        Assert.True(vm.HasNoApprover);
        Assert.False(vm.HasEmptyRoster);

        vm.Open();
        Assert.True(vm.HasEmptyRoster);
        Assert.False(vm.HasNoApprover);
    }

    // The view binds these, so a stale value is a silently wrong screen rather than a
    // test-only concern: filling the tile grid must switch the notice off by itself.
    [Fact]
    public async Task EmptyStateNotices_RaisePropertyChanged_WhenSellersArePopulated()
    {
        var vm = new SellerSwitchViewModel(await SessionWithRoster(), new FakeSellerRosterService());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Open();

        Assert.Contains(nameof(vm.HasNoApprover), changed);
        Assert.Contains(nameof(vm.HasEmptyRoster), changed);
        Assert.Contains(nameof(vm.CanSignOut), changed);
        Assert.False(vm.HasEmptyRoster);
    }

    // Extends the notice above to CanSignOut specifically, covering it alongside
    // HasNoApprover/HasEmptyRoster rather than only when a roster happens to repopulate:
    // reopening with a different canSignOut must actually notify, not just change the
    // property's live value underneath a stale binding. Not a test of Show()'s explicit
    // NotifyEmptyStateChanged() call in isolation — the roster here is non-empty in both
    // opens, so Sellers.Clear()+Add() also fires CollectionChanged both times (Clear()
    // raises a Reset unconditionally, roster or no roster), and that alone would already
    // make this pass. See Show()'s own remarks for why the explicit call still earns its
    // place regardless of that overlap.
    [Fact]
    public async Task CanSignOut_RaisesPropertyChanged_WhenReopenedWithADifferentPermission()
    {
        var session = await SessionWithRoster();
        await session.SwitchAsync("u-1", "4821");
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());
        vm.Open(canSignOut: true);
        Assert.True(vm.CanSignOut); // sanity check on the premise
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Open(canSignOut: false);

        Assert.Contains(nameof(vm.CanSignOut), changed);
        Assert.False(vm.CanSignOut);
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

    // ---------------------------------------------------------------------------------
    // Cancel (Part 0a): the overlay must always be dismissable, from either mode
    // (switch/approval) and either state (tile grid/PIN entry) — previously Back() only
    // demoted from the PIN pad to the tile grid, and nothing at all closed the overlay
    // short of finishing a PIN attempt.
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_FromTileGrid_HidesOverlay()
    {
        var vm = new SellerSwitchViewModel(await SessionWithRoster(), new FakeSellerRosterService());
        vm.Open();

        vm.CancelCommand.Execute(null);

        Assert.False(vm.IsVisible);
    }

    [Fact]
    public async Task Cancel_FromPinEntry_HidesOverlayDirectly_NotJustBackToTileGrid()
    {
        var vm = new SellerSwitchViewModel(await SessionWithRoster(), new FakeSellerRosterService());
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);
        Assert.True(vm.IsPinEntry); // sanity check on the premise

        vm.CancelCommand.Execute(null);

        Assert.False(vm.IsVisible);
    }

    [Fact]
    public async Task Cancel_InSwitchMode_LeavesCurrentSellerUnchanged()
    {
        var session = await SessionWithRoster();
        await session.SwitchAsync("u-1", "4821");
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());

        vm.Open(); // e.g. the cashier tapped the header chip out of curiosity
        vm.SelectSellerCommand.Execute(vm.Sellers[1]);
        vm.CancelCommand.Execute(null);

        Assert.Equal("u-1", session.Current?.Id); // still whoever was selected before
    }

    [Fact]
    public async Task Cancel_DuringApprovalMode_AbandonsTheOperation_ContinuationNeverRuns()
    {
        var vm = new SellerSwitchViewModel(await SessionWithRoster(), new FakeSellerRosterService());
        var ran = false;

        vm.OpenForApproval(s => s.CanCloseShift, _ => { ran = true; return Task.CompletedTask; });
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);
        vm.CancelCommand.Execute(null);

        Assert.False(vm.IsVisible);
        Assert.False(ran);
    }

    [Fact]
    public async Task Cancel_DuringApprovalMode_DiscardsContinuation_LaterUnrelatedApprovalDoesNotRunIt()
    {
        // The scenario the old shared-Approved-event + boolean-pending-flag design could
        // not rule out: cancel an approval, then complete a *different* one — the
        // abandoned operation's continuation must never fire just because some approval
        // eventually succeeded.
        var session = await SessionWithRoster();
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());
        var abandonedRan = false;
        var laterRan = false;

        vm.OpenForApproval(s => s.CanCloseShift, _ => { abandonedRan = true; return Task.CompletedTask; });
        vm.CancelCommand.Execute(null);

        vm.OpenForApproval(s => s.CanCloseShift, _ => { laterRan = true; return Task.CompletedTask; });
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);
        foreach (var d in "9073")
            await vm.AppendDigitCommand.ExecuteAsync(d.ToString());

        Assert.False(abandonedRan);
        Assert.True(laterRan);
    }

    [Fact]
    public async Task Open_WithContinuation_RunsItOnASuccessfulSwitch_CarryingTheSeller()
    {
        // The switch flow needs a continuation for the same reason approval mode has one:
        // PosViewModel.Pay stops and asks who is selling, and the payment has to resume off
        // the answer rather than make the cashier press Pay a second time.
        var session = await SessionWithRoster();
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());
        SellerInfo? resumedWith = null;
        var visibleWhenResumed = true;

        vm.Open(onSwitched: s =>
        {
            resumedWith = s;
            visibleWhenResumed = vm.IsVisible;
            return Task.CompletedTask;
        });
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);
        foreach (var d in "4821") // Sellers[0] is "u-1" Азиз — see SessionWithRoster
            await vm.AppendDigitCommand.ExecuteAsync(d.ToString());

        Assert.NotNull(resumedWith);
        Assert.Equal(vm.Sellers[0].Id, resumedWith!.Id);
        Assert.Same(resumedWith, session.Current); // the switch really happened
        // Closed before resuming: the operation must not come back up underneath a PIN pad.
        Assert.False(visibleWhenResumed);
    }

    [Fact]
    public async Task Open_WithContinuation_DiscardsItOnCancel()
    {
        // Dismissing the question abandons what was waiting on it — the cashier said no,
        // so the payment they were refused must not go through anyway.
        var session = await SessionWithRoster();
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());
        var ran = false;

        vm.Open(onSwitched: _ => { ran = true; return Task.CompletedTask; });
        vm.CancelCommand.Execute(null);

        // And a later switch, which nothing was waiting on, must not run the abandoned one.
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);
        foreach (var d in "4821") // Sellers[0] is "u-1" Азиз — see SessionWithRoster
            await vm.AppendDigitCommand.ExecuteAsync(d.ToString());

        Assert.Equal("u-1", session.Current?.Id); // the later switch really did succeed
        Assert.False(ran);
    }

    [Fact]
    public async Task Open_WithContinuation_DoesNotRunItOnAWrongPin()
    {
        var session = await SessionWithRoster();
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());
        var ran = false;

        vm.Open(onSwitched: _ => { ran = true; return Task.CompletedTask; });
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);
        foreach (var d in "0000")
            await vm.AppendDigitCommand.ExecuteAsync(d.ToString());

        Assert.False(ran);
        Assert.True(vm.HasError);
        Assert.True(vm.IsVisible); // still up for a retry, continuation still armed
    }

    [Fact]
    public async Task Cancel_WhileSubmitIsPending_IsANoOp()
    {
        var roster = new List<SellerInfo>
        {
            new() { Id = "u-1", FirstName = "Азиз", CanSell = true }
        };
        var session = new SlowSession(roster);
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);
        await vm.AppendDigitCommand.ExecuteAsync("1");
        await vm.AppendDigitCommand.ExecuteAsync("2");
        await vm.AppendDigitCommand.ExecuteAsync("3");
        var submitting = vm.AppendDigitCommand.ExecuteAsync("4"); // now mid-submit (_isBusy == true)

        vm.CancelCommand.Execute(null);
        Assert.True(vm.IsVisible); // Cancel is a no-op while busy, same as Back/Open

        session.CompleteSwitch(SwitchResult.Ok, vm.Sellers[0]);
        await submitting;

        Assert.False(vm.IsVisible); // the pending submit still resolves normally afterwards
    }

    // ---------------------------------------------------------------------------------
    // Manual sign-out (2026-07-31 design, manual counterpart to EndReceipt): the
    // tile-grid screen's "nobody is selling now" control. Never in approval mode — an
    // approval verifies a supervisor's PIN on someone else's behalf and deliberately
    // never touches Current, so signing out there would be nonsense. Never when the
    // caller (PosViewModel, via CanEndSellerSession) disallowed it, and never when
    // nobody is confirmed in the first place — nothing to sign out of.
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task SignOutSeller_ClearsCurrentSeller_AndHidesOverlay()
    {
        var session = await SessionWithRoster();
        await session.SwitchAsync("u-1", "4821");
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());
        vm.Open();

        vm.SignOutSellerCommand.Execute(null);

        Assert.Null(session.Current);
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public async Task CanSignOut_FalseInApprovalMode()
    {
        var session = await SessionWithRoster();
        await session.SwitchAsync("u-1", "4821");
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());

        vm.OpenForApproval(s => s.CanCloseShift);

        Assert.False(vm.CanSignOut);
    }

    [Fact]
    public async Task CanSignOut_FalseWhenCallerDisallowed()
    {
        var session = await SessionWithRoster();
        await session.SwitchAsync("u-1", "4821");
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());

        vm.Open(canSignOut: false);

        Assert.False(vm.CanSignOut);
    }

    [Fact]
    public async Task CanSignOut_FalseWhenNobodyConfirmed()
    {
        // SessionWithRoster never switches anyone on, so Current stays null here.
        var vm = new SellerSwitchViewModel(await SessionWithRoster(), new FakeSellerRosterService());

        vm.Open();

        Assert.False(vm.CanSignOut);
    }

    [Fact]
    public async Task Open_WithNoArgument_DefaultsToNotGrantingSignOut()
    {
        // The permissive default was exactly the shape of the raise-site bug the
        // critical fix closed: after that fix, the rule is that only a caller which
        // actually checked its own permission may grant sign-out, so a caller that
        // forgets the argument entirely must not get it for free. Someone is confirmed
        // here specifically to isolate the default from CanSignOut_FalseWhenNobodyConfirmed
        // above, which would pass regardless of the default since Current stays null there.
        var session = await SessionWithRoster();
        await session.SwitchAsync("u-1", "4821");
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());

        vm.Open();

        Assert.False(vm.CanSignOut);
    }

    [Fact]
    public async Task CanSignOut_TrueWhenAllowedAndSomeoneConfirmed()
    {
        var session = await SessionWithRoster();
        await session.SwitchAsync("u-1", "4821");
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());

        vm.Open(canSignOut: true);

        Assert.True(vm.CanSignOut);
    }

    [Fact]
    public async Task SignOutSeller_WhileSubmitIsPending_IsANoOp()
    {
        // Same shape as Cancel_WhileSubmitIsPending_IsANoOp above: SignOutSeller must
        // respect the _isBusy guard like every other mutating entry point in this class.
        var roster = new List<SellerInfo>
        {
            new() { Id = "u-1", FirstName = "Азиз", CanSell = true }
        };
        var session = new SlowSession(roster);
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());
        vm.Open();
        vm.SelectSellerCommand.Execute(vm.Sellers[0]);
        await vm.AppendDigitCommand.ExecuteAsync("1");
        await vm.AppendDigitCommand.ExecuteAsync("2");
        await vm.AppendDigitCommand.ExecuteAsync("3");
        var submitting = vm.AppendDigitCommand.ExecuteAsync("4"); // now mid-submit (_isBusy == true)

        vm.SignOutSellerCommand.Execute(null);
        Assert.True(vm.IsVisible); // SignOutSeller is a no-op while busy, same as Cancel/Back/Open

        session.CompleteSwitch(SwitchResult.Ok, vm.Sellers[0]);
        await submitting;

        Assert.False(vm.IsVisible); // the pending submit still resolves normally afterwards
    }

    // ---------------------------------------------------------------------------------
    // CanSignOut tracking ISellerSession.CurrentChanged (code-review fix): a roster
    // refresh can clear Current out from under an already-open overlay (see
    // SellerSession.LoadRosterAsync — a seller vanishing from the roster or losing
    // CanSell does exactly this), and without tracking the event the sign-out button
    // would keep showing with nobody left to sign out.
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task CanSignOut_RaisesPropertyChanged_WhenSessionCurrentChanges()
    {
        var session = await SessionWithRoster();
        await session.SwitchAsync("u-1", "4821");
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());
        vm.Open(canSignOut: true);
        Assert.True(vm.CanSignOut); // sanity check on the premise
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        session.Clear(); // e.g. a roster refresh dropping the current seller mid-overlay

        Assert.Contains(nameof(vm.CanSignOut), changed);
        Assert.False(vm.CanSignOut);
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromSellerSessionCurrentChanged()
    {
        // SellerSwitchViewModel is transient like PosViewModel, and ISellerSession is a
        // singleton — without unsubscribing, every login/logout cycle would leave one
        // more dead VM reacting to CurrentChanged forever (mirrors
        // PosViewModelSellerGateTest.Dispose_UnsubscribesFromSellerSessionCurrentChanged).
        var session = await SessionWithRoster();
        var vm = new SellerSwitchViewModel(session, new FakeSellerRosterService());
        vm.Dispose();

        var raised = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.CanSignOut)) raised = true; };

        await session.SwitchAsync("u-1", "4821");

        Assert.False(raised);
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
    public async Task PinCreation_WhileSetPinAsyncIsPending_TheOverlaySaysItIsWorking()
    {
        // The one place in this overlay that genuinely waits on the network. Verifying a
        // PIN is local and instant (see SellerSession), so a numpad that goes dead has to
        // mean something is happening — with nothing on screen saying so, the guard that
        // makes Back and the digits no-ops just reads as a frozen till.
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
        foreach (var d in "135") await vm.AppendDigitCommand.ExecuteAsync(d.ToString()); // confirm entry, 3 of 4

        Assert.False(vm.IsBusy); // nothing has suspended yet

        var submitting = vm.AppendDigitCommand.ExecuteAsync("7");
        Assert.True(vm.IsBusy);

        roster.CompleteSetPin(true);
        await submitting;

        Assert.False(vm.IsBusy);
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
