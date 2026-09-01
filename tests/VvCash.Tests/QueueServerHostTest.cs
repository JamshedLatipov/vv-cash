using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

/// <summary>Defect fix: a register that switches QueueRole to Server from the settings
/// screen used to get nothing — no port, no error, nothing — until it was closed and
/// reopened, because App.axaml.cs only ever created a QueueServer once, at startup, and
/// only if the role was already Server at that point. QueueServerHost is the live owner
/// that fixes this: it reconciles QueueServer and QueueFlushLoop against
/// IQueueSettings.QueueRole on every SettingsChanged, not just once at process start.
///
/// Each test drives an in-memory FakeSettings the same way the real settings screen's
/// Save() does (mutate fields, then call Save() to fire SettingsChanged) and then awaits
/// host.WaitForIdleAsync() — which performs no reconciliation of its own, only waits for
/// whatever SettingsChanged already triggered (see its own remarks for why that
/// distinction matters: a test that instead called the reconcile method directly would
/// stay green even if the SettingsChanged subscription itself were missing or broken,
/// because it would perform the reconciliation itself regardless of the event).</summary>
public class QueueServerHostTest
{
    private sealed class FakeSettings : ISettingsService, IQueueSettings
    {
        public string BackendUrl { get; set; } = "https://example.test/api/v1/";
        public string CashRegisterToken { get; set; } = string.Empty;
        public string AuthToken { get; set; } = string.Empty;
        public DateTime? AuthTokenExpiresAt { get; set; }
        public int SyncIntervalMinutes { get; set; } = 10;
        public string Language { get; set; } = "ru";
        public List<PrinterConfig> Printers { get; set; } = new();
        public bool ReturnOpenCashDrawer { get; set; } = true;
        public bool ReturnPrintReceipt { get; set; } = true;
        public string ExchangePayoutCategoryId { get; set; } = string.Empty;
        public string ReturnPayoutCategoryId { get; set; } = string.Empty;
        public string PhoneFormatId { get; set; } = string.Empty;
        public string CustomerDisplayPort { get; set; } = string.Empty;
        public int CustomerDisplayBaudRate { get; set; } = 9600;
        public string CustomerDisplayCodePageId { get; set; } = string.Empty;
        public string CustomerDisplayProtocolId { get; set; } = string.Empty;
        public string CustomerDisplayFramingId { get; set; } = string.Empty;
        public bool CustomerDisplayDtrRts { get; set; }

        public QueueRole QueueRole { get; set; } = QueueRole.Off;
        public string QueueServerAddress { get; set; } = string.Empty;
        public int QueuePort { get; set; }
        public string QueueSecret { get; set; } = string.Empty;
        public int TillIndex { get; set; }

        public event EventHandler? SettingsChanged;

        public void Save() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>QueueServerHost only needs FlushAsync to exist and return — the flush
    /// loop's own retry/backoff behaviour is QueueFlushLoop's concern (see its own tests),
    /// not this host's. The other three methods are never called by anything this test
    /// exercises; they are here only because IQueueClient requires them.</summary>
    private sealed class FakeQueueClient : IQueueClient
    {
        public Task<int?> IssueNumberAsync() => Task.FromResult<int?>(null);
        public Task<QueueOrder?> EnqueueAsync(SaleReceiptData sale) => Task.FromResult<QueueOrder?>(null);
        public Task FlushAsync() => Task.CompletedTask;
        public Task<int> PendingCountAsync() => Task.FromResult(0);
    }

    private static string TempDb() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vv-queue-host-{System.IO.Path.GetRandomFileName()}.db");

    /// <summary>Same trick QueueServerTest uses to name a free port in advance: needed
    /// here because a restart test has to ask for the SAME port twice (port 0 would hand
    /// out a different one the second time), and an "is it still closed" assertion needs
    /// a port number to probe before anything has opened it.</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static QueueServerHost NewHost(FakeSettings settings)
        => new(settings, new QueueStorage(TempDb()), new FakeQueueClient());

    private static async Task AssertPortClosedAsync(int port)
    {
        using var probe = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, port));
    }

    private static async Task AssertPortServesOrdersAsync(int port, string secret)
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        client.DefaultRequestHeaders.Add("X-Queue-Secret", secret);
        var response = await client.GetAsync("orders");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task SwitchingOffToServerOpensAPortThatWasNotOpenBefore()
    {
        var port = FreePort();
        var settings = new FakeSettings { QueueRole = QueueRole.Off, QueueSecret = "secret", QueuePort = port };
        var host = NewHost(settings);
        try
        {
            await host.WaitForIdleAsync();
            await AssertPortClosedAsync(port);

            settings.QueueRole = QueueRole.Server;
            settings.Save();
            await host.WaitForIdleAsync();

            await AssertPortServesOrdersAsync(port, "secret");
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [Fact]
    public async Task SwitchingServerToOffClosesThePort()
    {
        var port = FreePort();
        var settings = new FakeSettings { QueueRole = QueueRole.Server, QueueSecret = "secret", QueuePort = port };
        var host = NewHost(settings);
        try
        {
            await host.WaitForIdleAsync();
            await AssertPortServesOrdersAsync(port, "secret");

            settings.QueueRole = QueueRole.Off;
            settings.Save();
            await host.WaitForIdleAsync();

            await AssertPortClosedAsync(port);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    /// <summary>Fix 5's original complaint, now actually fixed: a secret changed on the
    /// settings screen used to keep the OLD secret live on the already-running Kestrel
    /// instance until the till was restarted. Same port both times (not 0) so the only
    /// thing that moves between the two requests below is the secret.</summary>
    [Fact]
    public async Task ChangingTheSecretStopsTheOldOneAndStartsTheNew()
    {
        var port = FreePort();
        var settings = new FakeSettings { QueueRole = QueueRole.Server, QueueSecret = "old-secret", QueuePort = port };
        var host = NewHost(settings);
        try
        {
            await host.WaitForIdleAsync();
            await AssertPortServesOrdersAsync(port, "old-secret");

            settings.QueueSecret = "new-secret";
            settings.Save();
            await host.WaitForIdleAsync();

            using var oldClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            oldClient.DefaultRequestHeaders.Add("X-Queue-Secret", "old-secret");
            var oldResponse = await oldClient.GetAsync("orders");
            Assert.Equal(HttpStatusCode.Unauthorized, oldResponse.StatusCode);

            await AssertPortServesOrdersAsync(port, "new-secret");
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    /// <summary>The decisive check is a live broadcast reaching an already-connected
    /// WebSocket, not ClientWebSocket.State: State is the client's own optimistic view and
    /// does not reliably reflect a passive server-side teardown until the client performs
    /// an operation and the failure surfaces (confirmed while writing this test — an
    /// earlier version of it asserted State == Open and stayed green even against a
    /// mutation that unconditionally restarted the server on every save). A restart, even
    /// on the identical port, hands the new QueueServer instance an empty subscriber list
    /// (see QueueServer's own _subscribers field), so THIS socket could never receive
    /// another push from it again — the only way this test can pass is if the same live
    /// instance, with this socket still in its subscriber list, is still the one running.</summary>
    [Fact]
    public async Task AnUnrelatedSaveLeavesARunningServerAlone()
    {
        var port = FreePort();
        var settings = new FakeSettings { QueueRole = QueueRole.Server, QueueSecret = "secret", QueuePort = port };
        var host = NewHost(settings);
        try
        {
            await host.WaitForIdleAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws?secret=secret"), cts.Token);
            var buffer = new byte[32 * 1024];
            await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token); // initial snapshot

            // Unrelated to the queue entirely — a printer/language edit elsewhere on the
            // same settings screen.
            settings.Language = "en";
            settings.Save();
            await host.WaitForIdleAsync();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            client.DefaultRequestHeaders.Add("X-Queue-Secret", "secret");
            var order = new QueueOrder
            {
                Id = Guid.NewGuid(),
                Number = 900,
                TillIndex = 0,
                State = QueueOrderState.New,
                CreatedAt = new DateTime(2026, 8, 31, 10, 0, 0),
                Lines = new List<QueueOrderLine> { new() { Name = "Coffee", Quantity = "1 pcs" } }
            };
            var post = await client.PostAsJsonAsync("orders", order);
            Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

            var push = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            var payload = Encoding.UTF8.GetString(buffer, 0, push.Count);
            Assert.Contains("900", payload);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    /// <summary>Two saves in quick succession — the second fired before the first's async
    /// Kestrel work has finished, the same way two taps of Save would look. Must converge
    /// to exactly the last-asked-for state: the first port must not still be listening
    /// (orphaned, nobody stopped it) and the second must be the one server actually
    /// reachable — not two servers, not zero.</summary>
    [Fact]
    public async Task OverlappingSavesConvergeToExactlyOneServerNeitherOrphanedNorDoubled()
    {
        var portA = FreePort();
        var portB = FreePort();
        var settings = new FakeSettings { QueueRole = QueueRole.Off, QueueSecret = "secret" };
        var host = NewHost(settings);
        try
        {
            await host.WaitForIdleAsync();

            settings.QueueRole = QueueRole.Server;
            settings.QueuePort = portA;
            settings.Save(); // fires SettingsChanged -> fire-and-forget reconcile #1

            settings.QueuePort = portB;
            settings.Save(); // fires SettingsChanged -> fire-and-forget reconcile #2, queued behind #1

            await host.WaitForIdleAsync(); // waits for both to fully settle

            await AssertPortClosedAsync(portA);
            await AssertPortServesOrdersAsync(portB, "secret");
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    /// <summary>Fix 4's other half, at the level this host actually owns: the screen must
    /// not keep showing an error after the thing it described has been fixed. Occupies the
    /// port with a plain TcpListener first so the real QueueServer's own bind fails for a
    /// reason outside this test's control, then frees it up and points the register at a
    /// different port — the same shape of fix an administrator would make on the settings
    /// screen (SettingsViewModelTest.Constructor_SurfacesTheQueueServerErrorWhenTheServerFailedToStart
    /// covers the view-model side of the same fix; this covers that the host it now reads
    /// from actually clears the error once the real problem is gone).</summary>
    [Fact]
    public async Task LastErrorReflectsTheCurrentAttemptNotAStaleOne()
    {
        var occupiedPort = FreePort();
        // A second real QueueServer as the blocker, not a raw TcpListener: a plain
        // TcpListener bound to the port did not reliably make Kestrel's own ListenAnyIP
        // bind fail on this machine (Windows' socket-sharing rules are permissive enough
        // that the two did not collide), whereas QueueServerTest's own occupied-port test
        // proves two QueueServers on the same port DO collide — same shape here, for the
        // same reliability.
        var blocker = new QueueServer(new QueueStorage(TempDb()), port: occupiedPort, secret: "blocker-secret");
        var blockerBound = await blocker.StartAsync();
        Assert.True(blockerBound >= 0); // sanity: the blocker itself must actually be listening

        try
        {
            var settings = new FakeSettings { QueueRole = QueueRole.Server, QueueSecret = "secret", QueuePort = occupiedPort };
            var host = NewHost(settings);
            try
            {
                await host.WaitForIdleAsync();
                Assert.False(string.IsNullOrEmpty(host.LastError));

                await blocker.StopAsync(); // the administrator's fix: free the port up
                settings.QueuePort = FreePort();
                settings.Save();
                await host.WaitForIdleAsync();

                Assert.Null(host.LastError);
            }
            finally
            {
                await host.ShutdownAsync();
            }
        }
        finally
        {
            await blocker.StopAsync();
        }
    }
}
