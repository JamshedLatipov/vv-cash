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

    /// <summary>Pins IsNullOrWhiteSpace over the cheaper IsNullOrEmpty: a server
    /// that pads full_name with spaces instead of omitting it must still fall
    /// back to FirstName/LastName, not surface a blank-looking name.</summary>
    [Fact]
    public void FullName_FallsBackToFirstAndLastName_WhenServerSendsWhitespaceOnly()
    {
        var response = new CounterpartyResponse { FirstName = "Иван", LastName = "Петров", FullNameRaw = "   " };

        Assert.Equal("Иван Петров", response.FullName);
    }
}
