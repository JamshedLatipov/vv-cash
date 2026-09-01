using System;
using System.Collections.Generic;
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
using VvCash.Services.Discounts;
using VvCash.Services.Hardware;
using VvCash.Services.Queue;
using VvCash.Services.Update;
using VvCash.ViewModels;
using VvCash.Views;

namespace VvCash;

public partial class App : Application
{
    public IServiceProvider? Services { get; private set; }

    // Held on the App instance, not a local in OnFrameworkInitializationCompleted: nothing
    // else in that method closes over this one, and a bare local with no reference keeping
    // it alive would be a live Kestrel listener one GC away from disappearing mid-run (the
    // DI container's own singleton cache would keep it alive too, but SettingsRequested
    // below reads its LastError on every open and a field reads more plainly than a fresh
    // GetRequiredService call each time). Owns QueueServer's and QueueFlushLoop's lifecycle
    // for the rest of the process, reconciling both against IQueueSettings.QueueRole on
    // every settings save — see QueueServerHost's own remarks for why a one-shot check here
    // at startup used to leave an administrator who flipped the role on a live till with
    // nothing listening until they closed and reopened the register.
    private QueueServerHost? _queueServerHost;

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
            // The register exits when its main window closes, full stop. The default is
            // OnLastWindowClose, and the customer display is deliberately hidden rather
            // than closed when it is not wanted (see NavigateToPos) — under the default
            // that hidden-but-open window would keep the process alive after the cashier
            // closed the register, background sync loop and all, with nothing on screen.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // The view model asks; the host decides how. Shutdown() rather than
            // MainWindow.Close() because the installer is already running and every
            // window has to go, not just the main one.
            var updateViewModel = Services.GetRequiredService<UpdateViewModel>();
            updateViewModel.ShutdownRequested = () => desktop.Shutdown();

            var initSettingsService = Services.GetRequiredService<ISettingsService>();
            I18nService.Instance.Initialize(string.IsNullOrEmpty(initSettingsService.Language) ? "ru" : initSettingsService.Language);

            // Queue server lifecycle (defect fix: switching the role to Server from the
            // settings screen used to open no port until the till was restarted — see
            // QueueServerHost's own remarks). Resolved eagerly here, not left to first use:
            // its constructor is what performs this startup reconciliation, fire-and-forget,
            // the same way QueueServer.StartAsync itself swallows its own failure instead of
            // throwing (an occupied port or an empty secret is a misconfiguration for the
            // settings screen to surface, not a reason the till itself must refuse to open).
            // From here on QueueServerHost keeps both QueueServer and QueueFlushLoop in sync
            // with IQueueSettings.QueueRole on every later settings save too, not just this
            // one at startup.
            _queueServerHost = Services.GetRequiredService<QueueServerHost>();

            // Шаблон читается из кэша один раз на старте и заново после каждой
            // успешной синхронизации. Подписка на ProductsSynced, а не инъекция
            // службы в PosViewModel: у той вью-модели полтора десятка мест
            // построения в тестах, и ещё один параметр конструктора обошёлся бы
            // дороже, чем эта строка.
            //
            // GetAwaiter().GetResult() вместо await: OnFrameworkInitializationCompleted
            // не async, а RefreshAsync читает только локальный SQLite — миллисекунды,
            // окно этим не задержит.
            var receiptTemplates = Services.GetRequiredService<IReceiptTemplateService>();
            receiptTemplates.RefreshAsync().GetAwaiter().GetResult();
            Services.GetRequiredService<ISyncService>().ProductsSynced +=
                async (_, _) => await receiptTemplates.RefreshAsync();

            var loginVm = Services.GetRequiredService<LoginViewModel>();
            var mainVm = Services.GetRequiredService<MainViewModel>();

            // PosViewModel is transient and subscribes to singleton service events;
            // dispose the prior instance before building a new one so a logout->login
            // cycle doesn't leave a dead VM reacting to events. (Not done in
            // MainViewModel.NavigateTo because the payment flow navigates away and back
            // to the *same* PosViewModel, which must survive that round-trip.)
            PosViewModel? activePosVm = null;
            // SellerSwitchViewModel now subscribes to ISellerSession.CurrentChanged too
            // (to keep CanSignOut live — see its own remarks), so it needs the exact same
            // dispose-before-replace treatment as activePosVm above, for the same reason.
            SellerSwitchViewModel? activeSellerSwitchVm = null;
            // One window for the whole run. It is NOT created here: Screens.All only reports
            // the real layout once MainWindow has actually opened — which is exactly why the
            // remembered-session path below defers NavigateToPos to MainWindow.Opened — so
            // the first NavigateToPos is the earliest moment this can be decided. Built on
            // demand there, reused afterwards, never rebuilt: it used to be constructed
            // fresh on every navigation with nothing holding the previous one, so a
            // logout->login cycle left another window on the customer's screen every time.
            CustomerDisplayWindow? customerWindow = null;
            void NavigateToPos()
            {
                activePosVm?.Dispose();
                activeSellerSwitchVm?.Dispose();

                var posVm = Services.GetRequiredService<PosViewModel>();
                activePosVm = posVm;
                posVm.NavigationRequest = mainVm.NavigateTo;

                // SellerSwitchViewModel is transient (like PosViewModel itself), so a fresh
                // one is resolved per NavigateToPos and handed to PosView via the
                // SellerSwitchViewModel property; PosViewModel only ever raises
                // SellerSwitchRequested to ask for it to open, matching how NavigationRequest
                // decouples PosViewModel from the mechanics of navigation.
                //
                // Built with ActivatorUtilities.CreateInstance rather than
                // Services.GetRequiredService<SellerSwitchViewModel>(): the .NET DI
                // container captures every IDisposable it constructs for its own eventual
                // disposal, root-provider-lifetime, regardless of whether this code ever
                // calls Dispose() itself — so GetRequiredService here would keep every
                // prior instance reachable (just inert) for as long as the app runs,
                // making activeSellerSwitchVm?.Dispose() above unable to actually release
                // one. ActivatorUtilities.CreateInstance constructs the instance directly
                // (still resolving its constructor's own dependencies from Services)
                // without registering it with the container, so this Dispose() call is
                // what it looks like: the only thing keeping the prior instance alive
                // once it stops being referenced here.
                var sellerSwitchVm = ActivatorUtilities.CreateInstance<SellerSwitchViewModel>(Services);
                activeSellerSwitchVm = sellerSwitchVm;
                posVm.SellerSwitchViewModel = sellerSwitchVm;
                // e.CanSignOut is decided by whichever PosViewModel method raised the
                // event, at the moment it raised it (see SellerSwitchRequest's own
                // remarks) — not read here from CanEndSellerSession, because by the time
                // this handler runs for AddToCart/ResumeParkedSale the cart is about to
                // gain an item that wasn't there yet when the gate fired. This wiring just
                // forwards the decision; PosViewModel still never learns
                // SellerSwitchViewModel exists.
                posVm.SellerSwitchRequested += (s, e) => sellerSwitchVm.Open(e.CanSignOut, e.OnSwitched);

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

                // The shift modal's manual sign-out, and the automatic recovery from a
                // server-rejected session (see PosViewModel.PerformSignOut/OnShiftSessionRevoked),
                // both converge here: PosViewModel can't navigate to loginVm itself because it
                // never holds that instance — it must be the *same* one constructed above, whose
                // LoginSuccessful handler below is already wired to NavigateToPos. An empty
                // explanation (the manual escape hatch) clears any stale error left over from a
                // previous failed login attempt rather than leaving it showing.
                posVm.LogoutRequested += (s, explanation) =>
                {
                    // The customer's screen must not keep showing the finished cart while
                    // the next cashier types their password.
                    customerWindow?.Hide();
                    loginVm.ErrorMessage = explanation;
                    mainVm.NavigateTo(loginVm);
                };

                // No LINQ: this file does not use it, and one loop is clearer than a cast
                // dance around a possibly-null Screens.All.
                var screenBounds = new List<PixelRect>();
                var allScreens = desktop.MainWindow?.Screens.All;
                if (allScreens != null)
                {
                    foreach (var screen in allScreens) screenBounds.Add(screen.Bounds);
                }

                var placement = CustomerDisplayPlacementSelector.Select(
                    Environment.GetEnvironmentVariable(CustomerDisplayPlacementSelector.OverrideVariable),
                    screenBounds);

                if (placement != null)
                {
                    if (customerWindow == null)
                    {
                        customerWindow = new CustomerDisplayWindow
                        {
                            WindowStartupLocation = WindowStartupLocation.Manual,
                            Position = placement.Position,
                        };

                        // Single-monitor debugging only. MainWindow is full-screen and
                        // Topmost, so a customer window merely placed beside it on the same
                        // screen would sit behind it and never be seen. Never true in
                        // production — see CustomerDisplayPlacementSelector.
                        if (placement.ForcedOnSingleScreen)
                        {
                            // CustomerDisplayWindow.axaml sets WindowState="FullScreen", and
                            // Avalonia ignores an explicit Width/Height while that is in
                            // force — without resetting it here, this branch would still
                            // produce a full-screen window instead of the modest overlay
                            // this comment promises.
                            customerWindow.WindowState = WindowState.Normal;
                            customerWindow.Topmost = true;
                            customerWindow.Width = 640;
                            customerWindow.Height = 400;
                        }

                        // This window is built once and reused for the whole run, so it must
                        // never actually close: Avalonia disposes a closed window's
                        // PlatformImpl irreversibly, and the next login's first Show() would
                        // throw "Cannot re-show a closed window" — with no unhandled-exception
                        // hook anywhere in this app, that takes the whole register down, not
                        // just the display. Reachable both ways: the title-bar X in the forced
                        // single-screen debug mode, and Alt+F4 on the ordinary full-screen one.
                        // Hiding is what every other caller here means by "make it go away".
                        customerWindow.Closing += (s, e) =>
                        {
                            e.Cancel = true;
                            ((Window)s!).Hide();
                        };
                    }

                    // The window survives; only what it shows is replaced. CustomerDisplayViewModel
                    // is transient, like PosViewModel itself, so each navigation brings a fresh one.
                    var customerVm = Services.GetRequiredService<CustomerDisplayViewModel>();
                    posVm.CustomerDisplayViewModel = customerVm;
                    customerWindow.DataContext = customerVm;

                    // SubscribeCustomerDisplayVisibility, not +=. The generated
                    // OnIsCustomerDisplayEnabledChanged fires only on a CHANGE, and
                    // ICashFeatureService is a singleton that survives logout->login — so the
                    // flag may already hold its final value by now and never raise anything at
                    // all. That method subscribes AND calls the handler with the current value,
                    // so the initial sync cannot be forgotten here.
                    var window = customerWindow;
                    posVm.SubscribeCustomerDisplayVisibility((s, visible) =>
                    {
                        if (visible) window.Show(); else window.Hide();
                    });
                }

                mainVm.CurrentViewModel = posVm;
            }

            loginVm.SettingsRequested += (s, e) =>
            {

                var settingsService = Services.GetRequiredService<ISettingsService>();
                var offlineStorage = Services.GetRequiredService<IOfflineStorageService>();
                var featuresForSettings = Services.GetRequiredService<ICashFeatureService>();
                var paymentCategories = Services.GetRequiredService<IPaymentCategoryService>();
                // LastError now lives on the live QueueServerHost singleton, which keeps
                // reconciling the actual server against settings for the whole run of the
                // process — not a value frozen the one time this used to be checked at
                // startup. Read fresh on every open, same as before: an administrator who
                // fixes the secret, saves (Save always navigates away — see GoBack in
                // SettingsViewModel.Save), and reopens this screen sees the CURRENT error,
                // because by then QueueServerHost has already reconciled to it.
                var settingsVm = new SettingsViewModel(loginVm, settingsService, offlineStorage, featuresForSettings, paymentCategories,
                    queueServerError: _queueServerHost?.LastError);
                settingsVm.NavigationRequest = mainVm.NavigateTo;
                mainVm.NavigateTo(settingsVm);
            };

            loginVm.LoginSuccessful += (s, e) => NavigateToPos();

            // Auto-login if a "Remember me" session is still valid. This is only a *local*
            // check — AuthTokenExpiresAt is a backstop against a shift that never gets closed
            // (see AuthService.LoginAsync's own remarks), not the server's real idea of when
            // the token dies, so this can still say "valid" for a token the server has already
            // revoked. Deliberately not validated against the server here: doing so would mean
            // either blocking startup on a network round-trip (this app must still launch with
            // no network — offline operation is the whole point) or firing an extra ping whose
            // only possible finding — "the token is dead" — NavigateToPos already discovers a
            // moment later anyway, for free, the instant PosViewModel's InitializeAsync calls
            // GetShiftStateAsync. That call's own 401 handling (ShiftService.SessionRevoked ->
            // PosViewModel.OnShiftSessionRevoked) is what actually closes the loop this gate
            // used to leave open: an optimistic wrong guess here now self-corrects within one
            // more round-trip instead of trapping the cashier behind a dead shift modal.
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
        services.AddSingleton<ICashFeatureService, CashFeatureService>();
        services.AddSingleton<IReceiptTemplateService, ReceiptTemplateService>();
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
        {
            b.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectCallback = async (ctx, ct) =>
                {
                    var addresses = await Dns.GetHostAddressesAsync(
                        ctx.DnsEndPoint.Host, AddressFamily.InterNetwork, ct);

                    // A host that resolves to no IPv4 address at all is a configuration
                    // problem, not an index to walk off the end of. Say so: indexing
                    // addresses[0] threw IndexOutOfRangeException from inside the
                    // connect callback, which surfaces as an unrelated-looking
                    // HttpRequestException several frames away.
                    if (addresses.Length == 0)
                        throw new SocketException((int)SocketError.HostNotFound);

                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };
                    try
                    {
                        // Every address, not just the first: a host behind several A
                        // records fails over instead of failing. The socket is disposed
                        // on the way out — this callback runs on the online-check loop
                        // every ten seconds, so a leak here is a leak per failed check.
                        await socket.ConnectAsync(addresses, ctx.DnsEndPoint.Port, ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            });

            // HttpClient's 100-second default is far too long for a till. The
            // online-check loop awaits its ping every ten seconds, so a server that
            // accepts connections and then stalls would freeze that loop — and with it
            // the online/offline indicator the cashier reads — for a minute and a half
            // at a time. UpdateService opts back out below.
            b.ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        });

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
        services.AddHttpClient<ICashOperationService, CashOperationService>().AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddHttpClient<IPaymentCategoryService, PaymentCategoryService>().AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddHttpClient<ISyncService, SyncService>().AddHttpMessageHandler<AuthHeaderHandler>();

        // Update services. Note the missing AddHttpMessageHandler<AuthHeaderHandler>():
        // every other client here talks to the register's own backend, but this one
        // talks to proffi.io, and the register's bearer token has no business going
        // to a host that is not our API.
        services.AddSingleton<IAppVersionProvider, AssemblyAppVersionProvider>();
        services.AddSingleton<IInstallerLauncher, ProcessInstallerLauncher>();
        // No 30-second cap here: this client downloads an installer, which legitimately
        // takes longer than that on a shop's connection. UpdateService bounds its own
        // calls — CheckAsync with a 10-second linked token, DownloadAsync with the
        // caller's, which the cashier's Cancel button drives.
        services.AddHttpClient<IUpdateService, UpdateService>()
            .ConfigureHttpClient(c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan);

        // POS Services
        services.AddHttpClient<IProductService, ProductService>().AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddSingleton<IPromotionProvider, PromotionProvider>();
        services.AddSingleton<ICartService, CartService>();
        services.AddSingleton<IParkedSaleService, ParkedSaleService>();
        services.AddHttpClient<IQuoteService, QuoteService>().AddHttpMessageHandler<AuthHeaderHandler>();

        // Hardware Services
        // Фабрика, а не регистрация по типу: составу принтеров нужен поставщик
        // шаблона. Поставщик, а не значение — шаблон приезжает синхронизацией в
        // произвольный момент и читается в момент печати, поэтому его смена
        // состав принтеров не пересобирает.
        services.AddSingleton<IPrinterService>(sp => new CompositePrinterService(
            sp.GetRequiredService<ISettingsService>(),
            printerFactory: null,
            template: () => sp.GetRequiredService<IReceiptTemplateService>().Current));
        services.AddSingleton<ICustomerDisplayService, ConfiguredCustomerDisplayService>();

        // Queue Services (Task 22/23). QueueStorage gets a factory rather than a type
        // registration: its single constructor takes an optional dbPath string, and the
        // container would otherwise try (and fail) to resolve that string as a service.
        // Registered as itself AND as IQueueStorage off the very same singleton — NumberPool
        // needs the concrete type (it reaches past the narrow storage interface for
        // ConnectionString — see NumberPool's own class remarks), while QueueClient and
        // QueueServer only need the interface, and both must still land on the one queue.db
        // this till uses.
        services.AddSingleton(_ => new QueueStorage());
        services.AddSingleton<IQueueStorage>(sp => sp.GetRequiredService<QueueStorage>());

        // SettingsService already implements IQueueSettings alongside ISettingsService (see
        // its own remarks) — resolved from that same singleton rather than a second
        // registration, so a role/port/secret edit on the settings screen is visible to the
        // queue immediately instead of to whichever interface happened to get its own copy.
        services.AddSingleton<IQueueSettings>(sp => (IQueueSettings)sp.GetRequiredService<ISettingsService>());

        // Singleton, not transient: NumberPool serialises issue/release through its own
        // in-process semaphore (see its own class remarks), which only means anything with
        // exactly one live instance for the whole till. TillIndex and QueueSecret are read
        // once here, matching NumberPool's own constructor (unlike HttpQueueTransport below,
        // which re-reads its settings on every call) — TillIndex is documented there as not
        // meant to move on a live till anyway.
        services.AddSingleton<INumberPool>(sp =>
        {
            var settings = sp.GetRequiredService<IQueueSettings>();
            return new NumberPool(
                sp.GetRequiredService<QueueStorage>(), settings.TillIndex, settings.QueueSecret, () => DateTime.Now);
        });

        // No AuthHeaderHandler on this client: it talks to another till's local queue server
        // (or, on the server till itself, to its own loopback port — see below), never to our
        // own backend, and has no business carrying that handler's bearer token.
        services.AddHttpClient("QueueClient");
        services.AddSingleton<IQueueTransport>(sp =>
        {
            var settings = sp.GetRequiredService<IQueueSettings>();
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("QueueClient");
            // The server till talks to itself through this same transport, over 127.0.0.1 on
            // its own port, rather than a "write straight to storage" shortcut for the one
            // role that happens to be local — one path means one set of failure modes, and
            // the loopback hop costs nothing. QueueServerAddress is what every OTHER till
            // points at this one with, so the server till's own value of that setting is
            // simply never read.
            return new HttpQueueTransport(
                http,
                () => settings.QueueRole == QueueRole.Server
                    ? $"127.0.0.1:{settings.QueuePort}"
                    : settings.QueueServerAddress,
                () => settings.QueueSecret);
        });

        services.AddSingleton<IQueueClient>(sp =>
        {
            var settings = sp.GetRequiredService<IQueueSettings>();
            return new QueueClient(
                sp.GetRequiredService<IQueueStorage>(),
                sp.GetRequiredService<INumberPool>(),
                sp.GetRequiredService<IQueueTransport>(),
                settings.TillIndex,
                () => DateTime.Now);
        });

        // Owns QueueServer's and QueueFlushLoop's lifecycle end to end — see its own
        // remarks. Plain constructor injection (ISettingsService, IQueueStorage,
        // IQueueClient are all registered above); OnFrameworkInitializationCompleted
        // resolves it eagerly right after the container is built, because its
        // constructor is what performs the startup reconciliation.
        services.AddSingleton<QueueServerHost>();

        // ViewModels
        services.AddTransient<LoginViewModel>();
        services.AddTransient<PosViewModel>();
        services.AddTransient<CustomerDisplayViewModel>();
        // SellerSwitchViewModel is deliberately NOT registered here: NavigateToPos below
        // builds it with ActivatorUtilities.CreateInstance instead of
        // GetRequiredService<SellerSwitchViewModel>() specifically so the container never
        // captures it for its own disposal — see that call site's own remarks. A
        // registration here would be dead (nothing resolves it through the container) and
        // would misleadingly suggest the container manages this type's lifetime.
        //
        // Singleton, unlike PosViewModel: a discovered update must survive navigation,
        // otherwise the badge disappears the moment the cashier opens returns.
        services.AddSingleton<UpdateViewModel>();
        services.AddSingleton<MainViewModel>();
    }
}
