using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VvCash.Models;

public partial class Product : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal? OriginalPrice { get; set; }
    public decimal? DiscountPercent { get; set; }
    public string ImagePath { get; set; } = string.Empty;
    public string Barcode { get; set; } = string.Empty;

    [ObservableProperty]
    private Bitmap? _imageBitmap;
}
