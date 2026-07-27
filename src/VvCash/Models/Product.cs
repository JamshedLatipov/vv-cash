using System.Collections.Generic;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VvCash.Models;

public partial class Product : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;

    /// <summary>Category <b>id</b>, not its display name — the cash sync sends the
    /// id under the "category" key. Promotion targeting matches against it.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Tag ids, for tag-targeted promotions evaluated offline.</summary>
    public List<string> TagIds { get; set; } = new();
    public decimal Price { get; set; }
    public decimal? OriginalPrice { get; set; }
    public decimal? DiscountPercent { get; set; }
    public string ImagePath { get; set; } = string.Empty;
    public string Barcode { get; set; } = string.Empty;

    [ObservableProperty]
    [property: System.Text.Json.Serialization.JsonIgnore]
    private Bitmap? _imageBitmap;
}
