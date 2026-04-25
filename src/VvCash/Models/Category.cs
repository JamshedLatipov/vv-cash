using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VvCash.Models;

public class CategoryImage
{
    public string? Path { get; set; }
}

public class CategoryRef
{
    public string? Id { get; set; }
}

public partial class Category : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public CategoryImage? Image { get; set; }
    public CategoryRef? Parent { get; set; }
    public string? ImageUrl { get; set; }

    [ObservableProperty]
    private Bitmap? _imageBitmap;
}
