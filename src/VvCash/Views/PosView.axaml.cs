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

    /// <summary>True while the modal seller-switch overlay is actually showing. Its PIN
    /// pad is touch-only — SellerSwitchView.axaml has no KeyBinding, no KeyDown handler,
    /// no code-behind at all — so nothing about the overlay itself stops keyboard input
    /// from reaching whatever's behind the scrim; every keyboard entry point in this file
    /// must check this before doing anything else. Checked once at the top of each
    /// handler rather than per-branch: an earlier version guarded only the barcode-Enter
    /// branch inside <see cref="OnGlobalKeyDown"/>, which left F2 (moves focus into the
    /// search box), F4 (fires PayCommand), Space/Enter on a focused product tile, and
    /// <see cref="OnSearchBoxKeyDown"/>'s own Enter-triggered scan all still reaching
    /// straight through.</summary>
    private bool IsSellerSwitchOverlayVisible()
        => DataContext is PosViewModel vm && vm.SellerSwitchViewModel is { IsVisible: true };

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        // Same guard, same reason, as the top of OnGlobalKeyDown: the overlay does not
        // capture focus, so a SearchBox that already had focus when the overlay opened —
        // or any path that reaches this handler without passing back through the
        // TopLevel's Tunnel handler below — must not be allowed to search or scan behind
        // the scrim. Escape is excluded so this handler's own Escape branch below (clear
        // the search box) keeps working exactly as it did before this guard existed;
        // OnGlobalKeyDown's own Escape branch, which dismisses the overlay itself, runs
        // first regardless (Tunnel fires before this Bubble-routed handler) and already
        // marks the event Handled when it acts, so the two never double-fire.
        if (e.Key != Key.Escape && IsSellerSwitchOverlayVisible())
        {
            // Drop whatever was mid-flight rather than leave it to replay itself the
            // moment the overlay closes and the next Enter lands — SearchQuery is
            // two-way bound to SearchBox (UpdateSourceTrigger=PropertyChanged), so
            // digits already sitting there don't clear themselves just because this
            // handler stops acting on them.
            if (DataContext is PosViewModel gateVm) gateVm.SearchQuery = string.Empty;
            e.Handled = true;
            return;
        }

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
        // The seller-switch overlay owns all keyboard input while it's showing, except
        // Escape (handled in its own branch below, where it dismisses the overlay).
        // See IsSellerSwitchOverlayVisible for why this has to be one check at the very
        // top rather than repeated per-branch.
        if (e.Key != Key.Escape && IsSellerSwitchOverlayVisible())
        {
            // Same reasoning as OnSearchBoxKeyDown's copy of this guard: drop any
            // mid-flight scan/search state instead of leaving it to silently replay once
            // the overlay closes. _barcodeBuffer is this handler's own buffer (see the
            // scanner logic further down) — SearchQuery never accumulates through it
            // directly, but is cleared here too as the same defensive belt-and-suspenders
            // OnSearchBoxKeyDown applies, since a scan can still be mid-flight there
            // (e.g. focus already in the search box before the overlay opened).
            _barcodeBuffer = string.Empty;
            if (DataContext is PosViewModel gateVm) gateVm.SearchQuery = string.Empty;
            e.Handled = true;
            return;
        }

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

        // Hardware barcode scanner: fast digit bursts terminated by Enter. Unreachable
        // while the overlay is visible — the guard at the top of this method already
        // returned — so no overlay check is needed here.
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
