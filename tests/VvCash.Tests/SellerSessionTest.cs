using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class SellerSessionTest
{
    // Same low-iteration PBKDF2 approach as PinHasherTest.Encode — only speed matters
    // for these fixtures, not cross-language byte-for-byte agreement.
    private static string Encode(string pin)
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        using var kdf = new Rfc2898DeriveBytes(pin, salt, 1000, HashAlgorithmName.SHA256);
        return $"pbkdf2_sha256$1000${Convert.ToBase64String(salt)}${Convert.ToBase64String(kdf.GetBytes(32))}";
    }

    private static List<SellerInfo> Roster() => new()
    {
        new SellerInfo { Id = "u-1", FirstName = "Азиз", PinHash = Encode("4821"), CanSell = true, MaxDiscount = 15 },
        new SellerInfo { Id = "u-2", FirstName = "Дилноза", PinHash = Encode("9073"), CanSell = true }
    };

    private static SellerSession NewSession(Func<DateTime> clock)
        => new(clock, TimeSpan.FromSeconds(90));

    [Fact]
    public async Task SwitchAsync_WithCorrectPin_SetsCurrentSeller()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());

        var result = await session.SwitchAsync("u-1", "4821");

        Assert.Equal(SwitchResult.Ok, result);
        Assert.Equal("u-1", session.Current?.Id);
    }

    [Fact]
    public async Task SwitchAsync_WithWrongPin_LeavesCurrentUnchanged()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());

        var result = await session.SwitchAsync("u-1", "0000");

        Assert.Equal(SwitchResult.WrongPin, result);
        Assert.Null(session.Current);
    }

    [Fact]
    public async Task SwitchAsync_LocksSellerAfterFiveFailures_ButNotADifferentSeller()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());

        for (var i = 0; i < 5; i++)
            await session.SwitchAsync("u-1", "0000");

        Assert.Equal(SwitchResult.Locked, await session.SwitchAsync("u-1", "4821"));
        // Lockout is per-seller: a locked seller must not block a colleague.
        Assert.Equal(SwitchResult.Ok, await session.SwitchAsync("u-2", "9073"));
    }

    [Fact]
    public async Task SwitchAsync_LockExpiresAfterSixtySeconds()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());

        for (var i = 0; i < 5; i++)
            await session.SwitchAsync("u-1", "0000");

        now = now.AddSeconds(61);

        Assert.Equal(SwitchResult.Ok, await session.SwitchAsync("u-1", "4821"));
    }

    [Fact]
    public async Task IsStale_BecomesTrueAfterIdleTimeout()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());
        await session.SwitchAsync("u-1", "4821");

        Assert.False(session.IsStale);

        now = now.AddSeconds(91);

        Assert.True(session.IsStale);
    }

    [Fact]
    public async Task Touch_ResetsIdleTimer()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());
        await session.SwitchAsync("u-1", "4821");

        now = now.AddSeconds(80);
        session.Touch();
        now = now.AddSeconds(80);

        Assert.False(session.IsStale);
    }

    [Fact]
    public async Task SwitchAsync_RaisesCurrentChangedOnceForSuccess_NotForFailure()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());

        var raised = 0;
        session.CurrentChanged += (_, _) => raised++;

        await session.SwitchAsync("u-1", "4821");
        await session.SwitchAsync("u-1", "0000");

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task SwitchAsync_ForUnknownSeller_ReturnsUnknownSeller_LeavesCurrentUnchanged_RaisesNoEvent()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());
        await session.SwitchAsync("u-1", "4821");

        var raised = 0;
        session.CurrentChanged += (_, _) => raised++;

        var result = await session.SwitchAsync("ghost", "4821");

        Assert.Equal(SwitchResult.UnknownSeller, result);
        Assert.Equal("u-1", session.Current?.Id);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task SwitchAsync_ForSellerWithoutPin_ReturnsPinNotSet()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(new List<SellerInfo>
        {
            new() { Id = "u-3", FirstName = "Новичок", PinHash = "", CanSell = true }
        });

        Assert.Equal(SwitchResult.PinNotSet, await session.SwitchAsync("u-3", "4821"));
    }

    [Fact]
    public async Task SwitchAsync_WithCorruptHash_DoesNotCountTowardLockout()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(new List<SellerInfo>
        {
            new() { Id = "u-4", FirstName = "Битый", PinHash = "not-a-valid-hash", CanSell = true }
        });

        // Six consecutive attempts, one more than MaxFailures (5) — if CorruptHash
        // were mistakenly counted like WrongPin this would flip to Locked partway
        // through instead of staying CorruptHash throughout.
        for (var i = 0; i < 6; i++)
            Assert.Equal(SwitchResult.CorruptHash, await session.SwitchAsync("u-4", "4821"));
    }

    [Fact]
    public async Task Clear_DropsCurrentSeller()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());
        await session.SwitchAsync("u-1", "4821");

        session.Clear();

        Assert.Null(session.Current);
    }

    [Fact]
    public async Task Clear_WithNoCurrentSeller_DoesNotRaiseCurrentChanged()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());

        var raised = 0;
        session.CurrentChanged += (_, _) => raised++;

        session.Clear();

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task ApproveAsync_WithValidPin_ReturnsApprover_AndLeavesCurrentUntouched()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());
        await session.SwitchAsync("u-1", "4821");

        var raised = 0;
        session.CurrentChanged += (_, _) => raised++;

        var approval = await session.ApproveAsync("u-2", "9073");

        Assert.Equal(SwitchResult.Ok, approval.Result);
        Assert.Equal("u-2", approval.Approver?.Id);
        // The acting seller (u-1) is unchanged by an escalation approval from u-2.
        Assert.Equal("u-1", session.Current?.Id);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task ApproveAsync_WithWrongPin_ReturnsWrongPinResult_AndNoApprover()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());

        var approval = await session.ApproveAsync("u-2", "0000");

        Assert.Equal(SwitchResult.WrongPin, approval.Result);
        Assert.Null(approval.Approver);
    }

    [Fact]
    public async Task ApproveAsync_WhenLocked_ReturnsLockedResult_AndNoApprover()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());

        for (var i = 0; i < 5; i++)
            await session.ApproveAsync("u-1", "0000");

        var approval = await session.ApproveAsync("u-1", "4821");

        Assert.Equal(SwitchResult.Locked, approval.Result);
        Assert.Null(approval.Approver);
    }

    [Fact]
    public async Task ApproveAsync_ForSellerWithoutPin_ReturnsPinNotSetResult()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(new List<SellerInfo>
        {
            new() { Id = "u-3", FirstName = "Новичок", PinHash = "", CanSell = true }
        });

        var approval = await session.ApproveAsync("u-3", "4821");

        Assert.Equal(SwitchResult.PinNotSet, approval.Result);
        Assert.Null(approval.Approver);
    }

    [Fact]
    public async Task ApproveAsync_ForUnknownSeller_ReturnsUnknownSellerResult()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());

        var approval = await session.ApproveAsync("ghost", "4821");

        Assert.Equal(SwitchResult.UnknownSeller, approval.Result);
        Assert.Null(approval.Approver);
    }

    [Fact]
    public async Task ApproveAsync_WithCorruptHash_ReturnsCorruptHashResult_AndNoApprover()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(new List<SellerInfo>
        {
            new() { Id = "u-4", FirstName = "Битый", PinHash = "not-a-valid-hash", CanSell = true }
        });

        var approval = await session.ApproveAsync("u-4", "4821");

        Assert.Equal(SwitchResult.CorruptHash, approval.Result);
        Assert.Null(approval.Approver);
    }

    // --- LoadRosterAsync reconciliation (removed/disabled current seller) ---

    [Fact]
    public async Task LoadRosterAsync_WhenCurrentSellerRemoved_ClearsCurrentAndRaisesEvent()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());
        await session.SwitchAsync("u-1", "4821");

        var raised = 0;
        session.CurrentChanged += (_, _) => raised++;

        // Reloaded roster no longer contains u-1 (e.g. an admin removed them
        // mid-shift).
        await session.LoadRosterAsync(new List<SellerInfo>
        {
            new() { Id = "u-2", FirstName = "Дилноза", PinHash = Encode("9073"), CanSell = true }
        });

        Assert.Null(session.Current);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task LoadRosterAsync_WhenCurrentSellerStillPresent_LeavesCurrentAndDoesNotRaiseEvent()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());
        await session.SwitchAsync("u-1", "4821");

        var raised = 0;
        session.CurrentChanged += (_, _) => raised++;

        // Reloaded roster still contains u-1 — nothing about the acting seller
        // should change.
        await session.LoadRosterAsync(Roster());

        Assert.Equal("u-1", session.Current?.Id);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task LoadRosterAsync_WhenCurrentSellerLosesCanSell_ClearsCurrent()
    {
        // Deliberate decision: present-but-not-sellable is treated the same as
        // absent for the purpose of staying Current, even though the backend's
        // GET /cashes/seller/ already filters on can_sell and should never
        // actually send such a row.
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());
        await session.SwitchAsync("u-1", "4821");

        await session.LoadRosterAsync(new List<SellerInfo>
        {
            new() { Id = "u-1", FirstName = "Азиз", PinHash = Encode("4821"), CanSell = false }
        });

        Assert.Null(session.Current);
    }

    [Fact]
    public async Task LoadRosterAsync_PrunesLockoutStateForVanishedSeller()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());

        // Lock u-1 out.
        for (var i = 0; i < 5; i++)
            await session.SwitchAsync("u-1", "0000");
        Assert.Equal(SwitchResult.Locked, await session.SwitchAsync("u-1", "4821"));

        // u-1 leaves the roster, then comes back (still within the 60s lock
        // window that would otherwise still be active) before it expires.
        await session.LoadRosterAsync(new List<SellerInfo>());
        await session.LoadRosterAsync(Roster());

        // No stale lock/failure count carried over — the correct PIN works
        // immediately instead of returning Locked.
        Assert.Equal(SwitchResult.Ok, await session.SwitchAsync("u-1", "4821"));
    }

    // --- ApproveAsync idle-timer interaction ---

    [Fact]
    public async Task ApproveAsync_WithValidPin_ResetsIdleTimer()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());
        await session.SwitchAsync("u-1", "4821");

        now = now.AddSeconds(80);
        await session.ApproveAsync("u-2", "9073");
        now = now.AddSeconds(80);

        // Had the approval not reset the timer, 160s since the switch would
        // already be stale (idle timeout is 90s).
        Assert.False(session.IsStale);
    }

    [Fact]
    public async Task ApproveAsync_WithWrongPin_DoesNotResetIdleTimer()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());
        await session.SwitchAsync("u-1", "4821");

        now = now.AddSeconds(91);
        await session.ApproveAsync("u-2", "0000");

        Assert.True(session.IsStale);
    }

    // --- Shared lockout counter between SwitchAsync and ApproveAsync ---

    [Fact]
    public async Task ApproveAsync_FiveFailures_LocksSubsequentSwitchAsync()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());

        for (var i = 0; i < 5; i++)
            await session.ApproveAsync("u-1", "0000");

        Assert.Equal(SwitchResult.Locked, await session.SwitchAsync("u-1", "4821"));
    }

    [Fact]
    public async Task SwitchAsync_FiveFailures_LocksSubsequentApproveAsync()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());

        for (var i = 0; i < 5; i++)
            await session.SwitchAsync("u-1", "0000");

        // Correct PIN, but the counter is shared: still locked from the
        // SwitchAsync failures above, so ApproveAsync must also refuse it.
        var approval = await session.ApproveAsync("u-1", "4821");
        Assert.Equal(SwitchResult.Locked, approval.Result);
        Assert.Null(approval.Approver);
    }
}
