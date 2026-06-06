using System;

namespace VvCash.Models;

/// <summary>Денормализованная строка списка отложенных чеков (для UI и БД).</summary>
public class ParkedSale
{
    public string Id { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? CustomerName { get; set; }
    public decimal Total { get; set; }
    public int ItemCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Payload { get; set; } = string.Empty; // JSON ParkedSaleSnapshot
}
