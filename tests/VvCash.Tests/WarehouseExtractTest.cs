using System.Text.Json;
using VvCash.Services.Api;
using Xunit;

namespace VvCash.Tests;

public class WarehouseExtractTest
{
    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

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
    public void ExtractWarehouseId_NullWhenAbsent()
    {
        Assert.Null(ShiftService.ExtractWarehouseId(Body("""{"id":"s1"}""")));
    }
}
