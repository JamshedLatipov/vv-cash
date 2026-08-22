using System.Threading.Tasks;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

public class CustomerDisplayTest
{
    [Fact]
    public async Task NullDisplay_ReportsSuccess()
    {
        // Касса без VFD — нормальное состояние, а не отказ.
        var display = new NullCustomerDisplayService();

        Assert.True(await display.ShowTotalAsync(100m));
        Assert.True(await display.ClearAsync());
    }

    [Fact]
    public async Task Vfd_OnAPortThatDoesNotExist_ReportsFailure()
    {
        // До правки SendAsync ловил всё и писал в Console, то есть отказ порта был
        // неотличим от успеха — ровно та болезнь, которую чинит проблема 1.
        var display = new VfdDisplayService("COM-does-not-exist", 9600, VvCash.Models.EscPosCodePages.Cp866);

        Assert.False(await display.ShowTotalAsync(100m));
    }

    [Fact]
    public async Task Vfd_DoesNotPrintADollarSign()
    {
        // Магазины не берут доллары; на чеке это уже чинили.
        var display = new VfdDisplayService("COM-does-not-exist", 9600, VvCash.Models.EscPosCodePages.Cp866);

        // Строка собирается до попытки открыть порт, поэтому её видно даже когда
        // отправка провалилась.
        Assert.DoesNotContain("$", display.LastRendered);
        await display.ShowTotalAsync(100m);
        Assert.DoesNotContain("$", display.LastRendered);
        Assert.Contains("100.00", display.LastRendered);
    }

    [Fact]
    public async Task Vfd_RendersTwentyColumnsPerLine()
    {
        var display = new VfdDisplayService("COM-does-not-exist", 9600, VvCash.Models.EscPosCodePages.Cp866);

        await display.ShowLineAsync("Молоко", "50.00");

        Assert.Equal(40, display.LastRendered.Length);
        Assert.StartsWith("Молоко              ", display.LastRendered);
    }
}
