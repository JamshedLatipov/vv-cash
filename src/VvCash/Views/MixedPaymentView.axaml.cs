using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VvCash.ViewModels;

namespace VvCash.Views;

public partial class MixedPaymentView : UserControl
{
    public MixedPaymentView()
    {
        InitializeComponent();

        var cashBorder = this.FindControl<Border>("CashBorder");
        var cardBorder = this.FindControl<Border>("CardBorder");
        var giftBorder = this.FindControl<Border>("GiftBorder");

        if (cashBorder != null) cashBorder.PointerPressed += Border_PointerPressed;
        if (cardBorder != null) cardBorder.PointerPressed += Border_PointerPressed;
        if (giftBorder != null) giftBorder.PointerPressed += Border_PointerPressed;

        var cashTextBox = this.FindControl<TextBox>("CashTextBox");
        var cardTextBox = this.FindControl<TextBox>("CardTextBox");
        var giftTextBox = this.FindControl<TextBox>("GiftTextBox");

        if (cashTextBox != null) cashTextBox.PointerPressed += Border_PointerPressed;
        if (cardTextBox != null) cardTextBox.PointerPressed += Border_PointerPressed;
        if (giftTextBox != null) giftTextBox.PointerPressed += Border_PointerPressed;
    }

    private void Border_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is Control control && DataContext is MixedPaymentViewModel viewModel)
        {
            if (control.Name == "CashBorder" || control.Name == "CashTextBox")
            {
                viewModel.SelectedMethod = "Cash";
            }
            else if (control.Name == "CardBorder" || control.Name == "CardTextBox")
            {
                viewModel.SelectedMethod = "Card";
            }
            else if (control.Name == "GiftBorder" || control.Name == "GiftTextBox")
            {
                viewModel.SelectedMethod = "Gift";
            }
        }
    }
}