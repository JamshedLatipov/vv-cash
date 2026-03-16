using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VvCash.ViewModels;

namespace VvCash.Views;

public partial class PosView : UserControl
{
    private string _barcodeBuffer = string.Empty;
    private DateTime _lastKeyTime = DateTime.MinValue;

    public PosView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel != null)
        {
            topLevel.AddHandler(InputElement.KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel != null)
        {
            topLevel.RemoveHandler(InputElement.KeyDownEvent, OnGlobalKeyDown);
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (sender is TextBox textBox && !string.IsNullOrWhiteSpace(textBox.Text))
            {
                var barcode = textBox.Text;
                if (DataContext is PosViewModel vm)
                {
                    _ = vm.HandleBarcodeAsync(barcode);
                    vm.SearchQuery = string.Empty;
                }
                e.Handled = true;
            }
        }
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastKeyTime).TotalMilliseconds;

        if (elapsed > 100)
        {
            _barcodeBuffer = string.Empty;
        }

        _lastKeyTime = now;

        if (e.Key == Key.Enter && !string.IsNullOrEmpty(_barcodeBuffer))
        {
            var barcode = _barcodeBuffer;
            _barcodeBuffer = string.Empty;
            if (DataContext is PosViewModel vm)
            {
                _ = vm.HandleBarcodeAsync(barcode);
                vm.SearchQuery = string.Empty; // clear out accidental typing in active search box
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

        if (ch != null)
        {
            _barcodeBuffer += ch;
        }
    }
}
