using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class SettingsDefaultsTest
{
    [Fact]
    public void PostReturnFlags_DefaultToTrue()
    {
        var data = new SettingsData();
        Assert.True(data.ReturnOpenCashDrawer);
        Assert.True(data.ReturnPrintReceipt);
    }

    [Fact]
    public void ExchangePayoutCategory_DefaultsToUnset()
    {
        // Unset is the only safe default: the exchange screen refuses while it is
        // empty, whereas guessing a category would file real money under the wrong
        // heading in every store that never configured one.
        Assert.Equal(string.Empty, new SettingsData().ExchangePayoutCategoryId);
    }

    [Fact]
    public void ReturnPayoutCategory_DefaultsToUnset()
    {
        // Same reasoning as the exchange one above, and the same consequence: an
        // upgraded register refuses returns until an administrator picks a category,
        // rather than quietly filing refunds under whatever came first.
        Assert.Equal(string.Empty, new SettingsData().ReturnPayoutCategoryId);
    }

    /// <summary>Пусто, а не "RU": дефолт живёт в PhoneFormats.Resolve, и второй
    /// его экземпляр здесь разъехался бы с первым при первой же правке.</summary>
    [Fact]
    public void PhoneFormat_DefaultsToUnset()
    {
        Assert.Equal(string.Empty, new SettingsData().PhoneFormatId);
    }

    /// <summary>9600 — единственный нетривиальный дефолт на все три новых поля:
    /// порт и код страницы просто пустые, а бод был зашит константой и легче
    /// прочего уехал бы молча при правке.</summary>
    [Fact]
    public void CustomerDisplay_DefaultsToNoPortAt9600Baud()
    {
        var data = new SettingsData();
        Assert.Equal(string.Empty, data.CustomerDisplayPort);
        Assert.Equal(9600, data.CustomerDisplayBaudRate);
        Assert.Equal(string.Empty, data.CustomerDisplayCodePageId);
    }
}
