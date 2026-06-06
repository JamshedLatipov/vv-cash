using System.Text.Json;
using VvCash.Models.Api;
using Xunit;

namespace VvCash.Tests;

public class ReturnModelsTest
{
    [Fact]
    public void DeserializesExpenseList()
    {
        const string json = """
        {"body":[{"selected_date":"2026-06-06T17:32:55.052Z","created_at":"2026-06-06T17:32:55.074858Z","id":"9abd5223-e6b1-4cc2-9075-fb128e0261cf","state":"PROCESSED","creator":"admin admin","counterparty":"UNDEFINED UNDEFINED","document_number":"9","cost":40,"to_pay":100,"discount":0,"payed":0,"remain":-100}],"page_count":1,"total_items":1,"item_per_page":10}
        """;
        var res = JsonSerializer.Deserialize<ExpenseListResponse>(json)!;
        Assert.Equal(1, res.PageCount);
        Assert.Single(res.Body);
        Assert.Equal("9abd5223-e6b1-4cc2-9075-fb128e0261cf", res.Body[0].Id);
        Assert.Equal("9", res.Body[0].DocumentNumber);
        Assert.Equal(100m, res.Body[0].ToPay);
    }

    [Fact]
    public void DeserializesReturnDetail()
    {
        const string json = """
        {"message":"success","body":{"id":"26f8d6e7-f46d-4431-b23b-8546b07cba54","details":[{"product":{"id":"6034b45e-daf6-4930-9827-a6fc082dd0dd","name":"Luxurious Rubber Salad","barcode":"77191819"},"id":"60a02d71-4f0b-4dd5-87f0-869a5d590d4d","quantity":3,"quantity_returned":1,"sold_price":100,"discount_in_unit":0,"after_discount":100,"discount_in_percent":0}]},"status":0}
        """;
        var res = JsonSerializer.Deserialize<ReturnDetailResponse>(json)!;
        Assert.Equal(0, res.Status);
        var line = Assert.Single(res.Body!.Details);
        Assert.Equal("6034b45e-daf6-4930-9827-a6fc082dd0dd", line.Product!.Id);
        Assert.Equal("Luxurious Rubber Salad", line.Product.Name);
        Assert.Equal(3, line.Quantity);
        Assert.Equal(1, line.QuantityReturned);
        Assert.Equal(100m, line.AfterDiscount);
    }

    [Fact]
    public void SerializesReturnRequest_SnakeCase()
    {
        var req = new ReturnRequest
        {
            SelectedDate = "2026-06-06",
            Details = { new ReturnLineRequest { Product = "p1", Quantity = 2 } }
        };
        var json = JsonSerializer.Serialize(req);
        Assert.Contains("\"selected_date\":\"2026-06-06\"", json);
        Assert.Contains("\"product\":\"p1\"", json);
        Assert.Contains("\"quantity\":2", json);
    }
}
