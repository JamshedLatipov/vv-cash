using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using VvCash.ViewModels;

namespace VvCash.Views;

public partial class ExchangeWindow : Window
{
    private ExchangeViewModel? _subscribedVm;

    public ExchangeWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChangedHandler;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unsubscribe();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnDataContextChangedHandler(object? sender, EventArgs e)
    {
        Unsubscribe();
        if (DataContext is ExchangeViewModel vm)
        {
            _subscribedVm = vm;
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

    // Auto-focuses the scan box the moment a receipt's lines land on screen. Unlike
    // PosView, this window has no global scanner-keystroke fallback (OnGlobalKeyDown),
    // so without this the cashier's first scan after picking a receipt has nowhere to
    // land unless they click into the box first.
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ExchangeViewModel.HasSelectedSale)) return;
        if (_subscribedVm?.HasSelectedSale != true) return;
        Dispatcher.UIThread.Post(() => ReturnScanBox.Focus());
    }

    private void OnReturnScanKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is ExchangeViewModel vm && vm.ScanReturnBarcodeCommand.CanExecute(null))
            vm.ScanReturnBarcodeCommand.Execute(null);
        e.Handled = true;
    }
}
