using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

/// <summary>SendViaLan's connect/write timeout (see EscPosPrinterService.LanTimeout).
///
/// The suite's usual trick for reaching a printer method without opening a socket —
/// (PrinterConnectionType)99, see CompositePrinterServiceTest and QueueDocumentsTest —
/// sends SendAsync down its default: branch before touching any transport at all, so
/// it cannot exercise a timeout: there is nothing there to time out. Every test below
/// opens a real loopback TcpListener instead.
///
/// LanTimeout defaults to 1s in production; every test here shrinks it (via the
/// internal setter) to a few tens of milliseconds so the suite does not pay a real
/// second per assertion. What is being proven is not "does this take ~1s" but "does
/// OUR cancellation end the attempt, rather than whatever the OS or the far end would
/// have done on their own" — shrinking the budget and confirming it actually cuts the
/// wait short is what tells the two apart.
///
/// No test here exercises the write half of SendViaLan against a genuine hang — see
/// the long comment on SendViaLan itself for why: on this machine's Windows loopback,
/// a single WriteAsync call never blocks regardless of payload size (tried up to
/// 128 MB) or socket buffer size, because loopback traffic takes a fast path that
/// bypasses ordinary flow control. Reproducing a real block took several separate
/// WriteAsync calls on one connection, a shape SendViaLan's single call does not
/// have. That is a property of this host's loopback adapter, not of SendViaLan or of
/// real network hardware — a NIC talking to an actual printer does not get the same
/// bypass. Writing a test that "passes" here without ever actually blocking would
/// prove nothing, so it is not included; the write path is protected by the same
/// CancellationTokenSource mechanism the connect tests below prove works.</summary>
public class EscPosLanTransportTest
{
    private static int ReserveClosedPort()
    {
        // Bind, learn the port, then stop listening — nothing answers there
        // afterwards, exactly like a printer that was unplugged after its address
        // was written into settings.json. On this hardware a closed loopback port
        // gets a startlingly slow ~2s RST (see other LAN tests in this suite and the
        // fix commit that added LanTimeout); a hung, unreachable address is worse
        // still. Either way, "the OS gets around to refusing it" is exactly what
        // LanTimeout exists to not wait for.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task ConnectTimeoutEndsThePrintInsteadOfWaitingForTheOsToRefuse()
    {
        var closedPort = ReserveClosedPort();
        var printer = new EscPosPrinterService(
            PrinterConnectionType.LAN, $"127.0.0.1:{closedPort}", EscPosCodePages.Default)
        {
            LanTimeout = TimeSpan.FromMilliseconds(150)
        };

        var sw = Stopwatch.StartNew();
        var ok = await printer.PrintTicketAsync("305");
        sw.Stop();

        // Lands exactly like any other transport failure: false and Error, the same
        // outcome PrintTicketAsync reports for a refused connection, a bad host name,
        // or the (PrinterConnectionType)99 branch used elsewhere in this suite. A
        // timeout is not a new kind of failure a caller has to learn to expect.
        Assert.False(ok);
        Assert.Equal(PrinterStatus.Error, printer.Status);

        // The natural OS refusal on this hardware is ~2s (measured; see the fix
        // commit). If this ever crept anywhere near that, LanTimeout stopped being
        // what ended the attempt, and the test would be measuring the OS's own
        // timeout again instead of proving anything about the fix.
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"expected the shrunk LanTimeout (150ms) to cut the wait short; took {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>PrintTestReceiptAsync is the one send path with no try/catch around it
    /// (see its own doc comment: the settings-screen test-print button wants a reason,
    /// not a swallowed bool). A timeout has to reach it as something callers of THAT
    /// method already know how to handle, which for an uncaught path means a standard,
    /// expected exception type — TimeoutException — carrying the address and which
    /// phase timed out, not a bare OperationCanceledException that reads like a
    /// cancellation nobody asked for.</summary>
    [Fact]
    public async Task ConnectTimeoutSurfacesAsATimeoutExceptionOnTheUncaughtPath()
    {
        var closedPort = ReserveClosedPort();
        var printer = new EscPosPrinterService(
            PrinterConnectionType.LAN, $"127.0.0.1:{closedPort}", EscPosCodePages.Default)
        {
            LanTimeout = TimeSpan.FromMilliseconds(150)
        };

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => printer.PrintTestReceiptAsync());
        Assert.Contains($"127.0.0.1:{closedPort}", ex.Message);
    }

    /// <summary>The other half of "cannot fail a healthy printer": a real accept-and-
    /// drain listener, well within LanTimeout's shrunk test budget, still reports
    /// success. Without this, a bug that made the timeout fire on every connect (not
    /// just a stuck one) would slip through — the two tests above only ever see a
    /// printer that is not there.</summary>
    [Fact]
    public async Task HealthyListenerStillPrintsWithinTheTimeout()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            using var stream = server.GetStream();
            var buffer = new byte[4096];
            // Drain whatever arrives, like a printer that actually reads its input,
            // until the client is done and closes its side.
            while (await stream.ReadAsync(buffer) > 0) { }
        });

        var printer = new EscPosPrinterService(
            PrinterConnectionType.LAN, $"127.0.0.1:{port}", EscPosCodePages.Default)
        {
            LanTimeout = TimeSpan.FromMilliseconds(150)
        };

        var ok = await printer.PrintTicketAsync("305");
        listener.Stop();
        await serverTask;

        Assert.True(ok);
        Assert.Equal(PrinterStatus.Ready, printer.Status);
    }
}
