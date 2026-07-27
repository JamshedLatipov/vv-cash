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

        var approver = await session.ApproveAsync("u-2", "9073");

        Assert.NotNull(approver);
        Assert.Equal("u-2", approver!.Id);
        // The acting seller (u-1) is unchanged by an escalation approval from u-2.
        Assert.Equal("u-1", session.Current?.Id);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task ApproveAsync_WithWrongPin_ReturnsNull()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = NewSession(() => now);
        await session.LoadRosterAsync(Roster());

        var approver = await session.ApproveAsync("u-2", "0000");

        Assert.Null(approver);
    }
}
