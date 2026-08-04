using Avalonia.Controls;
using Avalonia.Input;
using VvCash.ViewModels;

namespace VvCash.Views;

public partial class ReturnsWindow : Window
{
    public ReturnsWindow()
    {
        InitializeComponent();
    }

    private void OnReturnScanKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is ReturnsViewModel vm && vm.ScanReturnBarcodeCommand.CanExecute(null))
            vm.ScanReturnBarcodeCommand.Execute(null);
        e.Handled = true;
    }
}
