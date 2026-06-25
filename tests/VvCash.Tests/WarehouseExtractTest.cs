using System.Text.Json;
using VvCash.Services.Api;
using Xunit;

namespace VvCash.Tests;

public class WarehouseExtractTest
{
    // Clone so the element stays valid after the JsonDocument is disposed.
    private static JsonElement Body(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void ExtractWarehouseId_FromFlatField()
    {
        var id = ShiftService.ExtractWarehouseId(Body("""{"id":"s1","warehouse_id":"w-123"}"""));
        Assert.Equal("w-123", id);
    }

    [Fact]
    public void ExtractWarehouseId_FromNestedObject()
    {
        var id = ShiftService.ExtractWarehouseId(Body("""{"id":"s1","warehouse":{"id":"w-456","name":"Main"}}"""));
        Assert.Equal("w-456", id);
    }

    [Fact]
    public void ExtractWarehouseId_FromFlatWarehouseString()
    {
        var id = ShiftService.ExtractWarehouseId(Body("""{"id":"s1","warehouse":"w-789"}"""));
        Assert.Equal("w-789", id);
    }

    [Fact]
    public void ExtractWarehouseId_NullWhenAbsent()
    {
        Assert.Null(ShiftService.ExtractWarehouseId(Body("""{"id":"s1"}""")));
    }
}
