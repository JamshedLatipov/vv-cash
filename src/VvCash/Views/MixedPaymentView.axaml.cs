using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VvCash.ViewModels;

namespace VvCash.Views;

public partial class MixedPaymentView : UserControl
{
    public MixedPaymentView()
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

    // Keyboard entry: digits type into the active method, Backspace deletes,
    // Enter confirms once the balance is cleared, Esc goes back.
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MixedPaymentViewModel vm) return;

        switch (e.Key)
        {
            case Key.Enter:
                if (vm.IsFullyPaid)
                {
                    vm.ConfirmPaymentCommand.Execute(null);
                    e.Handled = true;
                }
                return;
            case Key.Escape:
                vm.BackCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.Back:
                vm.BackspaceCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.OemPeriod or Key.Decimal:
                vm.AddDigitCommand.Execute(".");
                e.Handled = true;
                return;
        }

        var digit = e.Key switch
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

        if (digit != null)
        {
            vm.AddDigitCommand.Execute(digit);
            e.Handled = true;
        }
    }
}
