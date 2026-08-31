using VvCash.Models;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

/// <summary>Перевод трёх галок в набор флагов и обратно. Отдельным тестом,
/// потому что XAML в этом проекте связывается отражением: опечатка в пути
/// биндинга собирается молча и падает только на точке.</summary>
public class SettingsViewModelRolesTest
{
    [Fact]
    public void CheckboxesBecomeFlags()
    {
        var vm = new PrinterConfigViewModel
        {
            PrintsReceipt = true,
            PrintsTicket = false,
            PrintsKitchenOrder = true
        };

        Assert.Equal(PrintRole.Receipt | PrintRole.KitchenOrder, vm.Roles);
    }

    [Fact]
    public void FlagsBecomeCheckboxes()
    {
        var vm = new PrinterConfigViewModel { Roles = PrintRole.Ticket };

        Assert.False(vm.PrintsReceipt);
        Assert.True(vm.PrintsTicket);
        Assert.False(vm.PrintsKitchenOrder);
    }

    [Fact]
    public void NoBoxTickedIsAValidConfiguration()
    {
        var vm = new PrinterConfigViewModel { Roles = PrintRole.Receipt };

        vm.PrintsReceipt = false;

        Assert.Equal(PrintRole.None, vm.Roles);
    }
}
