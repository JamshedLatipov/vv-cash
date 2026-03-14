using CommunityToolkit.Mvvm.ComponentModel;

namespace VvCash.Models;

public partial class CartItem : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineTotal))]
    private int _quantity;

    public Product Product { get; set; } = null!;
    public decimal LineTotal => Product.Price * Quantity;
    public decimal LineTotalOriginal => (Product.OriginalPrice ?? Product.Price) * Quantity;
}
