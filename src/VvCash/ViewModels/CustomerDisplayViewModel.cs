using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VvCash.Models;

namespace VvCash.ViewModels;

public partial class CustomerDisplayViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<CartItem> _items = new();

    [ObservableProperty]
    private decimal _total;

    [ObservableProperty]
    private bool _isIdle = true;

    [ObservableProperty]
    private string _welcomeMessage = "Welcome!";
}
