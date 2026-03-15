using System;
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
            var loginVm = new LoginViewModel();
            var mainVm = new MainViewModel(loginVm);

            loginVm.OpenSettingsRequested += (s, e) =>
            {
                var settingsVm = new SettingsViewModel();
                var settingsWindow = new SettingsWindow { DataContext = settingsVm };
                settingsVm.CloseRequested += (s2, e2) => settingsWindow.Close();

                settingsWindow.ShowDialog(desktop.MainWindow!);
            };

            loginVm.LoginSuccessful += (s, e) =>
            {
                var productService = new MockProductService();
                var cartService = new CartService();
                var discountService = new DiscountService();
                var printerService = new MockPrinterService();
                var customerDisplayService = new MockCustomerDisplayService();

                var posVm = new PosViewModel(productService, cartService, discountService, printerService, customerDisplayService);
                posVm.NavigationRequest = mainVm.NavigateTo;

                var screens = desktop.MainWindow?.Screens.All;
                if (screens != null && screens.Count > 1)
                {
                    var secondScreen = screens[1];
                    var customerVm = new CustomerDisplayViewModel();
                    posVm.CustomerDisplayViewModel = customerVm;
                    posVm.NavigationRequest = mainVm.NavigateTo;

                    var customerWindow = new CustomerDisplayWindow
                    {
                        DataContext = customerVm,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Position = new PixelPoint(secondScreen.Bounds.X, secondScreen.Bounds.Y)
                    };
                    customerWindow.Show();
                }

                mainVm.CurrentViewModel = posVm;
            };

            desktop.MainWindow = new MainWindow
            {
                DataContext = mainVm
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
