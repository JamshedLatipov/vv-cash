// tests/VvCash.Tests/QuoteModelsTest.cs
using System.Text.Json;
using VvCash.Models.Api;
using Xunit;

namespace VvCash.Tests;

public class QuoteModelsTest
{
    [Fact]
    public void QuoteResult_DeserializesSnakeCaseFromServer()
    {
        const string json = """
        {
          "quote_id":"q1","subtotal":100,"discount_total":15,"total":85,
          "lines":[{"product_id":"p1","quantity":2,"unit_price":50,"line_subtotal":100,
                    "discount_amount":15,"discount_percent":15,"final_line_total":85,
                    "source":{"kind":"card","ref":"c1"}}],
          "applied":[{"kind":"loyalty","amount":15,"ref":"c1"}],
          "rejected":[{"reason":"expired","ref":"PROMO5"}]
        }
        """;

        var r = JsonSerializer.Deserialize<QuoteResult>(json)!;

        Assert.Equal("q1", r.QuoteId);
        Assert.Equal(15m, r.DiscountTotal);
        Assert.Single(r.Lines);
        Assert.Equal("p1", r.Lines[0].ProductId);
        Assert.Equal(15m, r.Lines[0].DiscountPercent);
        Assert.Equal("card", r.Lines[0].Source!.Kind);
        Assert.Equal("loyalty", r.Applied[0].Kind);
        Assert.Equal("expired", r.Rejected[0].Reason);
    }

    [Fact]
    public void QuoteRequest_SerializesSnakeCaseAndOmitsNulls()
    {
        var req = new QuoteRequest
        {
            WarehouseId = "w1",
            Lines = new() { new QuoteLineInput { ProductId = "p1", Quantity = 1, UnitPrice = 10 } }
        };

        var json = JsonSerializer.Serialize(req);

        Assert.Contains("\"warehouse_id\":\"w1\"", json);
        Assert.Contains("\"product_id\":\"p1\"", json);
        Assert.DoesNotContain("card_identifier", json); // null опущен
        Assert.DoesNotContain("\"code\"", json);
    }
}
