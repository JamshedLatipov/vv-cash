using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using VvCash.Constants;
using VvCash.Services;
using System.Net.Http;
using VvCash.Services.Api;
using VvCash.Services.Data;
using VvCash.Services.Hardware;
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

            // PosViewModel is transient and subscribes to singleton service events;
            // dispose the prior instance before building a new one so a logout->login
            // cycle doesn't leave a dead VM reacting to events. (Not done in
            // MainViewModel.NavigateTo because the payment flow navigates away and back
            // to the *same* PosViewModel, which must survive that round-trip.)
            PosViewModel? activePosVm = null;
            void NavigateToPos()
            {
                activePosVm?.Dispose();

                var posVm = Services.GetRequiredService<PosViewModel>();
                activePosVm = posVm;
                posVm.NavigationRequest = mainVm.NavigateTo;

                // SellerSwitchViewModel is transient (like PosViewModel itself), so a fresh
                // one is resolved per NavigateToPos and handed to PosView via the
                // SellerSwitchViewModel property; PosViewModel only ever raises
                // SellerSwitchRequested to ask for it to open, matching how NavigationRequest
                // decouples PosViewModel from the mechanics of navigation.
                var sellerSwitchVm = Services.GetRequiredService<SellerSwitchViewModel>();
                posVm.SellerSwitchViewModel = sellerSwitchVm;
                posVm.SellerSwitchRequested += (s, e) => sellerSwitchVm.Open();

                // Closing a shift without CanCloseShift escalates through the same
                // overlay, in approval mode (see SellerSwitchViewModel.OpenForApproval).
                // The continuation passed here — not the shared Approved event — is what
                // makes approving actually finish the close instead of just dismissing
                // the overlay: OpenForApproval only invokes it when *this* approval
                // succeeds, so (unlike an earlier design built on a boolean pending flag)
                // a cancelled or unrelated approval can never trigger it. Returns and the
                // discount escalation below are wired the same way for the same reason.
                posVm.CloseShiftApprovalRequested += (s, e) => sellerSwitchVm.OpenForApproval(
                    x => x.CanCloseShift,
                    _ => posVm.OnCloseShiftApproved());

                // Opening returns without CanRefund escalates the same way — see
                // OpenReturns/ShowReturnsDialogAsync.
                posVm.RefundApprovalRequested += (s, e) => sellerSwitchVm.OpenForApproval(
                    x => x.CanRefund,
                    _ => posVm.ShowReturnsDialogAsync());

                // A manual discount above the ringing seller's own cap escalates the same
                // way, but with two twists: only a seller whose *own* cap covers the
                // requested percent may approve (a supervisor with no cap configured, or
                // too small a cap, cannot rubber-stamp this one), and the approved percent
                // travels with the event itself (EventHandler<decimal>) rather than through
                // a stored PosViewModel property — avoiding a field that could go stale
                // between the request and its approval.
                posVm.DiscountApprovalRequested += (s, percent) => sellerSwitchVm.OpenForApproval(
                    x => x.MaxDiscount > 0m && x.MaxDiscount >= percent,
                    approver => { posVm.ApplyApprovedDiscount(approver.Id, percent); return Task.CompletedTask; });

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
            }

            loginVm.SettingsRequested += (s, e) =>
            {

                var settingsService = Services.GetRequiredService<ISettingsService>();
                var offlineStorage = Services.GetRequiredService<IOfflineStorageService>();
                var settingsVm = new SettingsViewModel(loginVm, settingsService, offlineStorage);
                settingsVm.NavigationRequest = mainVm.NavigateTo;
                mainVm.NavigateTo(settingsVm);
            };

            loginVm.LoginSuccessful += (s, e) => NavigateToPos();

            // Auto-login if a "Remember me" session is still valid.
            bool rememberedSessionValid =
                !string.IsNullOrWhiteSpace(initSettingsService.AuthToken)
                && initSettingsService.AuthTokenExpiresAt.HasValue
                && initSettingsService.AuthTokenExpiresAt.Value > DateTime.UtcNow;

            desktop.MainWindow = new MainWindow
            {
                DataContext = mainVm
            };

            if (rememberedSessionValid)
            {
                // Defer until the window is open so multi-monitor detection works.
                desktop.MainWindow.Opened += (s, e) => NavigateToPos();
            }
            else
            {
                mainVm.CurrentViewModel = loginVm;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Core Services
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IOfflineStorageService, OfflineStorageService>();
        services.AddSingleton<ISessionContext, SessionContext>();

        // SellerSession's parameterless constructor is a test/manual-usage
        // convenience that hardcodes a 90s idle timeout; production must pass
        // the timeout explicitly (see the constructor's XML comment), so we
        // use a factory with the named constant instead of letting DI resolve
        // the parameterless ctor.
        services.AddSingleton<ISellerSession>(
            _ => new SellerSession(() => DateTime.UtcNow, SellerSessionConstants.IdleTimeout));

        services.AddTransient<AuthHeaderHandler>();

        // Force IPv4 on all HttpClients to avoid macOS SocketException
        // ('Can't assign requested address' caused by SocketsHttpHandler preferring IPv6)
        services.ConfigureHttpClientDefaults(b =>
            b.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectCallback = async (ctx, ct) =>
                {
                    var addresses = await Dns.GetHostAddressesAsync(
                        ctx.DnsEndPoint.Host, AddressFamily.InterNetwork, ct);
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };
                    await socket.ConnectAsync(addresses[0], ctx.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
            }));

        services.AddHttpClient("DefaultClient").AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddTransient(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("DefaultClient"));

        services.AddSingleton<IAuthService, AuthService>();
        services.AddHttpClient<ICategoryService, CategoryService>().AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddHttpClient<ICounterpartyService, CounterpartyService>().AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddHttpClient<IShiftService, ShiftService>().AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddHttpClient<ISellerRosterService, SellerRosterService>().AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddHttpClient<ExpenseDocumentService>().AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddSingleton<IExpenseDocumentService>(sp => sp.GetRequiredService<ExpenseDocumentService>());
        services.AddHttpClient<IReturnService, ReturnService>().AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddHttpClient<ISyncService, SyncService>().AddHttpMessageHandler<AuthHeaderHandler>();

        // POS Services
        services.AddHttpClient<IProductService, ProductService>().AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddSingleton<ICartService, CartService>();
        services.AddSingleton<IParkedSaleService, ParkedSaleService>();
        services.AddHttpClient<IQuoteService, QuoteService>().AddHttpMessageHandler<AuthHeaderHandler>();

        // Hardware Services
        services.AddSingleton<IPrinterService, CompositePrinterService>();
        services.AddSingleton<ICustomerDisplayService, MockCustomerDisplayService>();

        // ViewModels
        services.AddTransient<LoginViewModel>();
        services.AddTransient<PosViewModel>();
        services.AddTransient<CustomerDisplayViewModel>();
        services.AddTransient<SellerSwitchViewModel>();
        services.AddSingleton<MainViewModel>();
    }
}
