using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VvCash.ViewModels;

namespace VvCash.Views;

public partial class PosView : UserControl
{
    private string _barcodeBuffer = string.Empty;
    private DateTime _lastKeyTime = DateTime.MinValue;
    private int _prevCartCount;
    private PosViewModel? _subscribedVm;

    public PosView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChangedHandler;
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
        Unsubscribe();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnDataContextChangedHandler(object? sender, EventArgs e)
    {
        Unsubscribe();
        if (DataContext is PosViewModel vm)
        {
            _subscribedVm = vm;
            _prevCartCount = vm.CartItems.Count;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void Unsubscribe()
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm = null;
        }
    }

    // Auto-scroll to the newest cart line so the cashier never hunts for it.
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PosViewModel.CartItems)) return;
        var vm = _subscribedVm;
        if (vm == null) return;
        var count = vm.CartItems.Count;
        var grew = count > _prevCartCount;
        _prevCartCount = count;
        Dispatcher.UIThread.Post(() =>
        {
            if (grew) CartScroll.ScrollToEnd();
            UpdatePagerVisibility();
        }, DispatcherPriority.Loaded);
    }

    private static bool LooksLikeBarcode(string text)
    {
        // Real barcodes are digit-only (EAN-8/13, UPC…). Anything else is a text search.
        if (text.Length < 4) return false;
        foreach (var c in text)
        {
            if (!char.IsDigit(c)) return false;
        }
        return true;
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (sender is TextBox textBox && !string.IsNullOrWhiteSpace(textBox.Text))
            {
                var text = textBox.Text.Trim();
                if (DataContext is PosViewModel vm)
                {
                    if (LooksLikeBarcode(text))
                    {
                        // Digits → barcode lookup (exact match, alert when missing).
                        _ = vm.HandleBarcodeAsync(text);
                        vm.SearchQuery = string.Empty;
                    }
                    // Text → keep the live-filtered catalog; do NOT treat it as a
                    // barcode (that always ended in "Товар не найден").
                }
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (DataContext is PosViewModel vm)
            {
                vm.SearchQuery = string.Empty;
            }
            e.Handled = true;
        }
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        // Hotkeys: F2 focus search · F4 pay · Esc clear search
        if (e.Key == Key.F2)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F4)
        {
            if (DataContext is PosViewModel payVm && payVm.PayCommand.CanExecute(null))
            {
                payVm.PayCommand.Execute(null);
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (DataContext is PosViewModel escVm)
            {
                // The seller-switch overlay is modal and covers the whole screen (see
                // PosView.axaml's ZIndex 4000) — while it's up, Escape dismisses it
                // (mirrors SellerSwitchView's own close button, reachable from either
                // overlay state, not just the tile grid) rather than falling through to
                // clear the search box underneath it.
                var overlay = escVm.SellerSwitchViewModel;
                if (overlay != null && overlay.IsVisible)
                {
                    overlay.CancelCommand.Execute(null);
                    e.Handled = true;
                    return;
                }

                if (!string.IsNullOrEmpty(escVm.SearchQuery))
                {
                    escVm.SearchQuery = string.Empty;
                    e.Handled = true;
                }
            }
            return;
        }

        // Hardware barcode scanner: fast digit bursts terminated by Enter.
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
                // Same overlay-visibility guard as the Escape branch above: this handler
                // is Tunnel-routed on the TopLevel, so a hardware scanner's keystrokes
                // reach here regardless of focus and regardless of whether the modal
                // seller-switch overlay is up. Without this, a scan while the overlay is
                // showing reaches HandleBarcodeAsync -> AddToCart and fills the cart out
                // from under it — including, now that the overlay can show a sign-out
                // control, putting that control over a cart that already has an item.
                var overlay = vm.SellerSwitchViewModel;
                if (overlay == null || !overlay.IsVisible)
                {
                    _ = vm.HandleBarcodeAsync(barcode);
                    vm.SearchQuery = string.Empty; // clear out accidental typing in active search box
                }
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

    // ---- Kiosk-friendly cart paging: big up/down buttons instead of finger-drag scrolling ----

    private void OnCartScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdatePagerVisibility();
    }

    private void UpdatePagerVisibility()
    {
        var offset = CartScroll.Offset.Y;
        var viewport = CartScroll.Viewport.Height;
        var extent = CartScroll.Extent.Height;
        PagerUp.IsVisible = offset > 4;
        PagerDown.IsVisible = offset + viewport < extent - 4;
    }

    private void PageBy(double direction)
    {
        var viewport = CartScroll.Viewport.Height;
        var target = Math.Max(0, CartScroll.Offset.Y + direction * viewport * 0.8);
        CartScroll.Offset = CartScroll.Offset.WithY(target);
        UpdatePagerVisibility();
    }

    private void OnPagerUpClick(object? sender, RoutedEventArgs e) => PageBy(-1);

    private void OnPagerDownClick(object? sender, RoutedEventArgs e) => PageBy(1);
}
