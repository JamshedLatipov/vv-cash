using Avalonia.Controls;
using Avalonia.Input;
using VvCash.ViewModels;

namespace VvCash.Views;

public partial class ExchangeWindow : Window
{
    public ExchangeWindow()
    {
        InitializeComponent();
    }

    private void OnReturnScanKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is ExchangeViewModel vm && vm.ScanReturnBarcodeCommand.CanExecute(null))
            vm.ScanReturnBarcodeCommand.Execute(null);
        e.Handled = true;
    }
}
