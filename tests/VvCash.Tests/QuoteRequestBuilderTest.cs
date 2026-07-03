using System.Collections.Generic;
using VvCash.Models;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class QuoteRequestBuilderTest
{
    private static List<CartItem> Cart() => new()
    {
        new CartItem { Product = new Product { Id = "p1", Price = 10m }, Quantity = 2 },
        new CartItem { Product = new Product { Id = "p2", Price = 5m }, Quantity = 1 },
    };

    [Fact]
    public void Build_MapsLinesAndIdentifiers()
    {
        var req = QuoteRequestBuilder.Build(Cart(), "w1", "CARD-7", "PROMO5");

        Assert.Equal("w1", req.WarehouseId);
        Assert.Equal("CARD-7", req.CardIdentifier);
        Assert.Equal("PROMO5", req.Code);
        Assert.Equal(2, req.Lines.Count);
        Assert.Equal("p1", req.Lines[0].ProductId);
        Assert.Equal(2m, req.Lines[0].Quantity);
        Assert.Equal(10m, req.Lines[0].UnitPrice);
        Assert.Equal("p2", req.Lines[1].ProductId);
        Assert.Equal(1m, req.Lines[1].Quantity);
        Assert.Equal(5m, req.Lines[1].UnitPrice);
    }

    [Fact]
    public void Build_BlankCardAndCodeBecomeNull()
    {
        var req = QuoteRequestBuilder.Build(Cart(), "w1", "  ", "");

        Assert.Null(req.CardIdentifier);
        Assert.Null(req.Code);
    }
}
