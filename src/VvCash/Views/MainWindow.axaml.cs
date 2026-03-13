using System;
using Avalonia.Controls;
using Avalonia.Input;
using VvCash.ViewModels;

namespace VvCash.Views;

public partial class MainWindow : Window
{
    private string _barcodeBuffer = string.Empty;
    private DateTime _lastKeyTime = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnWindowKeyDown;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastKeyTime).TotalMilliseconds;

        if (elapsed > 100 && !string.IsNullOrEmpty(_barcodeBuffer))
        {
            _barcodeBuffer = string.Empty;
        }

        _lastKeyTime = now;

        if (e.Key == Key.Enter && !string.IsNullOrEmpty(_barcodeBuffer))
        {
            var barcode = _barcodeBuffer;
            _barcodeBuffer = string.Empty;
            if (DataContext is MainWindowViewModel vm)
            {
                _ = vm.HandleBarcodeAsync(barcode);
            }
            e.Handled = true;
            return;
        }

        var ch = e.Key switch
        {
            Key.D0 or Key.NumPad0 => "0",
            Key.D1 or Key.NumPad1 => "1",
            Key.D2 or Key.NumPad2 => "2",
            Key.D3 or Key.NumPad3 => "3",
            Key.D4 or Key.NumPad4 => "4",
            Key.D5 or Key.NumPad5 => "5",
            Key.D6 or Key.NumPad6 => "6",
            Key.D7 or Key.NumPad7 => "7",
            Key.D8 or Key.NumPad8 => "8",
            Key.D9 or Key.NumPad9 => "9",
            _ => null
        };

        if (ch != null && elapsed < 100)
        {
            _barcodeBuffer += ch;
        }
    }
}
