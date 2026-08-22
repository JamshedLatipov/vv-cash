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
    public async Task Vfd_WhenThePortCannotBeOpened_ReportsFailure()
    {
        // До правки SendAsync ловил всё и писал в Console, то есть отказ порта был
        // неотличим от успеха — ровно та болезнь, которую чинит проблема 1.
        //
        // Имя заведомо некорректное, а не просто отсутствующее: Open() бросит
        // ArgumentException, тогда как выдернутый COM3 бросил бы FileNotFoundException.
        // Ловятся одинаково, но имя вида COM250 на чужой машине может внезапно
        // разрешиться в настоящее железо, а это — никогда.
        var display = new VfdDisplayService("COM-does-not-exist", 9600, VvCash.Models.EscPosCodePages.Cp866);

        Assert.False(await display.ShowTotalAsync(100m));
    }

    [Fact]
    public void Vfd_DoesNotPrintADollarSign()
    {
        // Магазины не берут доллары; на чеке это уже чинили. Money() отсюда не видна —
        // она private в VfdDisplayService, — поэтому сумма ниже отформатирована так же,
        // как её отдал бы Money(100m): F2, инвариантная культура, без "$". Проверяем,
        // что BuildFrame эту сумму не портит и символ не добавляет.
        var frame = VfdDisplayService.BuildFrame("TOTAL", 100m.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

        Assert.DoesNotContain("$", frame);
        Assert.Contains("100.00", frame);
    }

    [Fact]
    public void Vfd_RendersTwentyColumnsPerLine()
    {
        // 40 и число пробелов ниже привязаны к Columns = 20 в VfdDisplayService.
        // Columns — private const и тесту не виден: если значение изменится, здесь
        // просто перестанет совпадать длина, без подсказки почему.
        var frame = VfdDisplayService.BuildFrame("Молоко", "50.00");

        Assert.Equal(40, frame.Length);
        Assert.StartsWith("Молоко" + new string(' ', 14), frame);
    }
}
