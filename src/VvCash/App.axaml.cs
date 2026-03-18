using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using VvCash.Services;
using System.Net.Http;
using VvCash.Services.Api;
using VvCash.Services.Data;
using VvCash.Services.Hardware;
using VvCash.Services.Api;
using VvCash.ViewModels;
using VvCash.Views;

namespace VvCash;

public partial class App : Application
{
    public IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var initSettingsService = Services.GetRequiredService<ISettingsService>();
            I18nService.Instance.Initialize(string.IsNullOrEmpty(initSettingsService.Language) ? "ru" : initSettingsService.Language);

            var loginVm = Services.GetRequiredService<LoginViewModel>();
            var mainVm = Services.GetRequiredService<MainViewModel>();

            mainVm.CurrentViewModel = loginVm;

            loginVm.SettingsRequested += (s, e) =>
            {

                var settingsService = Services.GetRequiredService<ISettingsService>();
                var settingsVm = new SettingsViewModel(loginVm, settingsService);
                settingsVm.NavigationRequest = mainVm.NavigateTo;
                mainVm.NavigateTo(settingsVm);
            };

            loginVm.LoginSuccessful += (s, e) =>
            {
                var posVm = Services.GetRequiredService<PosViewModel>();
                posVm.NavigationRequest = mainVm.NavigateTo;

                var screens = desktop.MainWindow?.Screens.All;
                if (screens != null && screens.Count > 1)
                {
                    var secondScreen = screens[1];
                    var customerVm = Services.GetRequiredService<CustomerDisplayViewModel>();
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

    private void ConfigureServices(IServiceCollection services)
    {
        // Core Services
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IOfflineStorageService, OfflineStorageService>();

        services.AddTransient<AuthHeaderHandler>();
        services.AddHttpClient("DefaultClient").AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddTransient(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("DefaultClient"));

        services.AddSingleton<IAuthService, AuthService>();
        services.AddHttpClient<ICategoryService, CategoryService>().AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddHttpClient<ICounterpartyService, CounterpartyService>().AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddHttpClient<IShiftService, ShiftService>().AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddHttpClient<ExpenseDocumentService>().AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddSingleton<IExpenseDocumentService>(sp => sp.GetRequiredService<ExpenseDocumentService>());
        services.AddHttpClient<ISyncService, SyncService>().AddHttpMessageHandler<AuthHeaderHandler>();

        // POS Services
        services.AddHttpClient<IProductService, ProductService>().AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddSingleton<ICartService, CartService>();
        services.AddSingleton<IDiscountService, DiscountService>();

        // Hardware Services
        services.AddSingleton<IPrinterService, CompositePrinterService>();
        services.AddSingleton<ICustomerDisplayService, MockCustomerDisplayService>();

        // ViewModels
        services.AddTransient<LoginViewModel>();
        services.AddTransient<PosViewModel>();
        services.AddTransient<CustomerDisplayViewModel>();
        services.AddSingleton<MainViewModel>();
    }
}
