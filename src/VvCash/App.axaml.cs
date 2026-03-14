using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VvCash.Services;
using VvCash.Services.Hardware;
using VvCash.ViewModels;
using VvCash.Views;

namespace VvCash;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var productService = new MockProductService();
            var cartService = new CartService();
            var discountService = new DiscountService();
            var printerService = new MockPrinterService();
            var customerDisplayService = new MockCustomerDisplayService();

            var mainVm = new MainWindowViewModel(productService, cartService, discountService, printerService, customerDisplayService);
            var mainWindow = new MainWindow { DataContext = mainVm };
            desktop.MainWindow = mainWindow;

            mainWindow.Opened += (s, e) =>
            {
                var screens = mainWindow.Screens.All;
                if (screens.Count > 1)
                {
                    var secondScreen = screens[1];
                    var customerVm = new CustomerDisplayViewModel();
                    mainVm.CustomerDisplayViewModel = customerVm;
                    var customerWindow = new CustomerDisplayWindow
                    {
                        DataContext = customerVm,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Position = new PixelPoint(secondScreen.Bounds.X, secondScreen.Bounds.Y)
                    };
                    customerWindow.Show();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
