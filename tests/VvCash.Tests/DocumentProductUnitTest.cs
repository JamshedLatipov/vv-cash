using System.Text.Json;
using VvCash.Models.Api;
using Xunit;

namespace VvCash.Tests;

// The server takes unit_id, unit_factor and quantity_in_unit together or not at
// all, and rejects the document on a partial trio. These tests pin the two
// shapes that may go on the wire.
public class DocumentProductUnitTest
{
    private static string Serialize(DocumentProduct p) => JsonSerializer.Serialize(p);

    [Fact]
    public void ProductLine_OmitsTheWholeTrio_WhenSoldByThePiece()
    {
        var json = Serialize(new DocumentProduct { ProductId = "p1", Quantity = 2m, SellPrice = 10m });

        Assert.DoesNotContain("unit_id", json);
        Assert.DoesNotContain("unit_factor", json);
        Assert.DoesNotContain("quantity_in_unit", json);
    }

    [Fact]
    public void ProductLine_CarriesTheWholeTrio_WhenSoldInAUnit()
    {
        var json = Serialize(new DocumentProduct
        {
            ProductId = "p1",
            Quantity = 53m,
            SellPrice = 100m,
            UnitId = "u-1",
            UnitFactor = 0.24m,
            QuantityInUnit = 12.72m,
        });

        Assert.Contains("\"unit_id\":\"u-1\"", json);
        Assert.Contains("\"unit_factor\":0.24", json);
        Assert.Contains("\"quantity_in_unit\":12.72", json);
        // quantity stays pieces either way.
        Assert.Contains("\"quantity\":53", json);
    }
}
