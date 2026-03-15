using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VvCash.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private ViewModelBase? _currentViewModel;

    public MainViewModel()
    {
    }

    public void NavigateTo(ViewModelBase viewModel)
    {
        CurrentViewModel = viewModel;
    }
}
