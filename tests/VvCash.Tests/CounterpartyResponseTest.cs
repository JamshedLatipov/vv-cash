using VvCash.Models.Api;
using Xunit;

namespace VvCash.Tests;

public class CounterpartyResponseTest
{
    [Fact]
    public void FullName_FallsBackToFirstAndLastName_WhenServerOmitsIt()
    {
        var response = new CounterpartyResponse { FirstName = "Иван", LastName = "Петров" };

        Assert.Equal("Иван Петров", response.FullName);
    }

    [Fact]
    public void FullName_PrefersTheServersOwnValue_WhenPresent()
    {
        var response = new CounterpartyResponse
        {
            FirstName = "Иван", LastName = "Петров", FullNameRaw = "Петров Иван Иванович",
        };

        Assert.Equal("Петров Иван Иванович", response.FullName);
    }
}
