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
    public async Task AppendDigit_RapidRepeatOnFourthDigit_DoesNotDoubleSubmit()
    {
        // SellerSession's own Task-returning members are backed by
        // Task.FromResult/Task.CompletedTask (see its class remarks) — no
        // real suspension point — so an `await` on them completes
        // synchronously within the current call. That means a command
        // invocation started on the UI thread runs the whole
        // AppendDigit -> SubmitAsync -> SwitchAsync chain to completion
        // before control returns to the caller, and by the time a second,
        // near-simultaneous tap is dispatched, Pin has already reached
        // PinLength, so AppendDigitAsync's own `Pin.Length >= PinLength`
        // guard rejects it. This test simulates the worst case — starting
        // both calls before awaiting either — to confirm no double switch
        // happens.
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
}
