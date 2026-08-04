using System.Collections.Generic;
using System.Text;
using VvCash.Models;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

public class EscPosExchangeTest
{
    [Fact]
    public void ExchangeReceiptBuffer_CustomerOwes_ContainsBothSectionsAndAmountDue()
    {
        var returned = new List<ReturnReceiptLine> { new("Old Shirt", 1, 80m) };
        var issued = new List<ReturnReceiptLine> { new("New Shirt", 1, 130m) };

        var bytes = EscPosPrinterService.BuildExchangeReceipt(returned, issued, 50m, "9");
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("Old Shirt", text);
        Assert.Contains("New Shirt", text);
        Assert.Contains("#9", text);
        Assert.Contains("DUE", text); // amount-due label
        Assert.Contains("50", text);
    }

    [Fact]
    public void ExchangeReceiptBuffer_TillOwes_PrintsAbsoluteAmount_NeverTheMinusSign()
    {
        var returned = new List<ReturnReceiptLine> { new("Old Shirt", 1, 130m) };
        var issued = new List<ReturnReceiptLine> { new("New Shirt", 1, 90m) };

        var bytes = EscPosPrinterService.BuildExchangeReceipt(returned, issued, -40m, "9");
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("REFUND", text); // refund label carries the direction
        Assert.Contains("40", text);
        // The sign belongs to the label, not the number the cashier reads out.
        Assert.DoesNotContain("-40", text);
    }

    [Fact]
    public void ExchangeReceiptBuffer_EvenSwap_DoesNotPrintARefundOfZero()
    {
        var returned = new List<ReturnReceiptLine> { new("Old Shirt", 1, 90m) };
        var issued = new List<ReturnReceiptLine> { new("New Shirt", 1, 90m) };

        var bytes = EscPosPrinterService.BuildExchangeReceipt(returned, issued, 0m, "9");
        var text = Encoding.UTF8.GetString(bytes);

        // Nothing is owed either way, so neither label applies — "REFUND: 0.00" reads
        // as a refund the customer never gets.
        Assert.DoesNotContain("REFUND", text);
        Assert.DoesNotContain("DUE", text);
        Assert.Contains("NO DIFFERENCE", text);
    }

    [Fact]
    public void ExchangeReceiptBuffer_IncludesWarehouseSellerAndDate_WhenGiven()
    {
        var returned = new List<ReturnReceiptLine> { new("Old Shirt", 1, 80m) };
        var issued = new List<ReturnReceiptLine> { new("New Shirt", 1, 130m) };

        var bytes = EscPosPrinterService.BuildExchangeReceipt(
            returned, issued, 50m, "9", warehouseName: "Central Store", sellerName: "Ivanov I.", saleDate: "06.06.2026 17:32");
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("Central Store", text);
        Assert.Contains("Ivanov I.", text);
        Assert.Contains("06.06.2026 17:32", text);
    }
}
