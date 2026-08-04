using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace VvCash.Models.Api;

// GET /documents/expense/?page=
public class ExpenseListResponse
{
    [JsonPropertyName("body")] public List<ExpenseListItem> Body { get; set; } = new();
    [JsonPropertyName("page_count")] public int PageCount { get; set; }
    [JsonPropertyName("total_items")] public int TotalItems { get; set; }
    [JsonPropertyName("item_per_page")] public int ItemPerPage { get; set; }
}

public class ExpenseListItem
{
    [JsonPropertyName("selected_date")] public string? SelectedDate { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("creator")] public string? Creator { get; set; }
    [JsonPropertyName("counterparty")] public string? Counterparty { get; set; }
    [JsonPropertyName("warehouse_name")] public string? WarehouseName { get; set; }
    [JsonPropertyName("document_number")] public string? DocumentNumber { get; set; }
    [JsonPropertyName("cost")] public decimal Cost { get; set; }
    [JsonPropertyName("to_pay")] public decimal ToPay { get; set; }
    [JsonPropertyName("discount")] public decimal Discount { get; set; }
    [JsonPropertyName("payed")] public decimal Payed { get; set; }
    [JsonPropertyName("remain")] public decimal Remain { get; set; }

    /// <summary>SelectedDate formatted for a cashier to read. The API sends UTC
    /// ISO-8601 (e.g. "2026-06-06T17:32:55.052Z"); both the sale-picker card and
    /// the printed return/exchange receipt were showing that raw string verbatim.
    /// Falls back to the raw string when it doesn't parse, rather than showing
    /// nothing.</summary>
    [JsonIgnore]
    public string FormattedSelectedDate
        => DateTimeOffset.TryParse(SelectedDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
            : SelectedDate ?? string.Empty;
}

// GET /documents/return/{id}/
public class ReturnDetailResponse
{
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("body")] public ReturnDetailBody? Body { get; set; }
    [JsonPropertyName("status")] public int Status { get; set; }
}

public class ReturnDetailBody
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("details")] public List<ReturnDetailLine> Details { get; set; } = new();
}

public class ReturnDetailLine
{
    [JsonPropertyName("product")] public ReturnProduct? Product { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("quantity")] public int Quantity { get; set; }
    [JsonPropertyName("quantity_returned")] public int QuantityReturned { get; set; }
    [JsonPropertyName("sold_price")] public decimal SoldPrice { get; set; }
    [JsonPropertyName("discount_in_unit")] public decimal DiscountInUnit { get; set; }
    [JsonPropertyName("after_discount")] public decimal AfterDiscount { get; set; }
    [JsonPropertyName("discount_in_percent")] public decimal DiscountInPercent { get; set; }
}

public class ReturnProduct
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("barcode")] public string? Barcode { get; set; }
    [JsonPropertyName("article")] public string? Article { get; set; }
}

// POST /documents/return/{id}/
public class ReturnRequest
{
    [JsonPropertyName("selected_date")] public string SelectedDate { get; set; } = string.Empty;
    [JsonPropertyName("details")] public List<ReturnLineRequest> Details { get; set; } = new();
}

public class ReturnLineRequest
{
    [JsonPropertyName("product")] public string Product { get; set; } = string.Empty;
    [JsonPropertyName("quantity")] public int Quantity { get; set; }
}
