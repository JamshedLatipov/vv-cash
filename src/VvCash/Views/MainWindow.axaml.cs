using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using VvCash.ViewModels;

namespace VvCash.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        // this.AttachDevTools();
#endif
    }

    /// <summary>Closing the window by its X (or Alt+F4) asks the same question the register's
    /// power button does — end the shift, hand over, or shut down — instead of dropping out
    /// from under an open shift. Only while the POS is on screen: the login view has no
    /// session or shift to reason about, so there it closes as before.
    ///
    /// PosViewModel.IsExitConfirmed is what stops this from being a trap. The menu's own
    /// "close the program" branch calls Window.Close(), which re-enters here; the flag is set
    /// before that call so this pass lets it through.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is MainViewModel { CurrentViewModel: PosViewModel pos } && !pos.IsExitConfirmed)
        {
            e.Cancel = true;
            pos.OpenExitMenuCommand.Execute(null);
            return;
        }

        base.OnClosing(e);
    }
}
