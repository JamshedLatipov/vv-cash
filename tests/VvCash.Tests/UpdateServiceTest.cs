using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VvCash.Services.Update;
using Xunit;

namespace VvCash.Tests;

public class UpdateServiceTest
{
    private sealed class FakeVersionProvider : IAppVersionProvider
    {
        public FakeVersionProvider(string version) => Current = Version.Parse(version);
        public Version Current { get; }
    }

    private const string ValidHash =
        "9f2b1c4a7e35d081bc6f42a90e5713d8cf20ab6749e83c15d02f7ba418c69e3d";

    private static string Manifest(
        string product = "vvcash",
        string version = "1.1.0",
        string url = "https://proffi.io/downloads/proffi-kassa-setup.exe",
        string sha256 = ValidHash)
        => $$"""
        {
          "product": "{{product}}",
          "version": "{{version}}",
          "url": "{{url}}",
          "sha256": "{{sha256}}",
          "sizeBytes": 35651584,
          "notes": "test build"
        }
        """;

    private static (UpdateService Service, StubHttpMessageHandler Handler) Build(
        string body,
        string currentVersion = "1.0.0",
        HttpStatusCode status = HttpStatusCode.OK,
        string contentType = "application/json")
    {
        var handler = new StubHttpMessageHandler(_ => (status, body)) { ContentType = contentType };
        var service = new UpdateService(
            new HttpClient(handler),
            new FakeVersionProvider(currentVersion));
        return (service, handler);
    }

    [Fact]
    public async Task NewerVersionIsOffered()
    {
        var (service, _) = Build(Manifest(version: "1.1.0"), currentVersion: "1.0.0");

        var info = (await service.CheckAsync(CancellationToken.None)).Update;

        Assert.NotNull(info);
        Assert.Equal(new Version(1, 1, 0), info!.Version);
        Assert.Equal("https://proffi.io/downloads/proffi-kassa-setup.exe", info.Url);
        Assert.Equal(ValidHash, info.Sha256);
        Assert.Equal(35651584, info.SizeBytes);
        Assert.Equal("test build", info.Notes);
    }

    [Fact]
    public async Task SameVersionIsNotOffered()
    {
        // The register runs 1.0.0, which the build stamps as 1.0.0.0. Without
        // normalisation on both sides this comparison would call the running build
        // newer than the release it was cut from.
        var (service, _) = Build(Manifest(version: "1.0.0"), currentVersion: "1.0.0.0");

        Assert.Null((await service.CheckAsync(CancellationToken.None)).Update);
    }

    [Fact]
    public async Task OlderVersionIsNotOffered()
    {
        var (service, _) = Build(Manifest(version: "0.9.0"), currentVersion: "1.0.0");

        Assert.Null((await service.CheckAsync(CancellationToken.None)).Update);
    }

    [Fact]
    public async Task HtmlUnderStatus200IsRejected()
    {
        // proffi.io is a single-page app: any path it does not recognise comes back as
        // index.html with status 200. A check that trusted the status code would hand
        // markup to the JSON parser.
        var (service, _) = Build(
            "<!doctype html><html lang=\"ru\"><head><title>CRM</title></head></html>",
            contentType: "text/html");

        Assert.Null((await service.CheckAsync(CancellationToken.None)).Update);
    }

    [Fact]
    public async Task ManifestForAnotherProductIsRejected()
    {
        var (service, _) = Build(Manifest(product: "softphone"));

        Assert.Null((await service.CheckAsync(CancellationToken.None)).Update);
    }

    [Fact]
    public async Task PlainHttpUrlIsRejected()
    {
        var (service, _) = Build(Manifest(url: "http://proffi.io/downloads/proffi-kassa-setup.exe"));

        Assert.Null((await service.CheckAsync(CancellationToken.None)).Update);
    }

    [Fact]
    public async Task RelativeUrlIsRejected()
    {
        var (service, _) = Build(Manifest(url: "/downloads/proffi-kassa-setup.exe"));

        Assert.Null((await service.CheckAsync(CancellationToken.None)).Update);
    }

    [Fact]
    public async Task DifferentHostUrlIsRejected()
    {
        // The manifest lives on proffi.io. If whatever writes kassa-latest.json has
        // broader access than whatever publishes to /downloads/ (a CI job, a CMS, a
        // webhook), pinning the host keeps that weaker attacker from pointing the
        // register at their own server.
        var (service, _) = Build(Manifest(url: "https://evil.example/downloads/proffi-kassa-setup.exe"));

        Assert.Null((await service.CheckAsync(CancellationToken.None)).Update);
    }

    [Fact]
    public async Task SameHostDifferentPathIsAccepted()
    {
        // The check pins the host, not the whole URL — a future re-organisation of the
        // download directory must not silently stop all updates.
        var (service, _) = Build(Manifest(url: "https://proffi.io/downloads/v2/proffi-kassa-setup.exe"));

        Assert.NotNull((await service.CheckAsync(CancellationToken.None)).Update);
    }

    [Fact]
    public async Task HostCasingIsIgnored()
    {
        var (service, _) = Build(Manifest(url: "https://PROFFI.IO/downloads/proffi-kassa-setup.exe"));

        Assert.NotNull((await service.CheckAsync(CancellationToken.None)).Update);
    }

    [Theory]
    [InlineData("")]
    [InlineData("deadbeef")]
    [InlineData("9f2b1c4a7e35d081bc6f42a90e5713d8cf20ab6749e83c15d02f7ba418c69e3")]
    [InlineData("zzzz1c4a7e35d081bc6f42a90e5713d8cf20ab6749e83c15d02f7ba418c69e3d")]
    public async Task MalformedHashIsRejected(string sha256)
    {
        var (service, _) = Build(Manifest(sha256: sha256));

        Assert.Null((await service.CheckAsync(CancellationToken.None)).Update);
    }

    [Fact]
    public async Task UnparsableVersionIsRejected()
    {
        var (service, _) = Build(Manifest(version: "next-friday"));

        Assert.Null((await service.CheckAsync(CancellationToken.None)).Update);
    }

    [Fact]
    public async Task BrokenJsonIsRejected()
    {
        var (service, _) = Build("{ \"product\": \"vvcash\", ");

        Assert.Null((await service.CheckAsync(CancellationToken.None)).Update);
    }

    [Fact]
    public async Task NotFoundIsRejected()
    {
        var (service, _) = Build(Manifest(), status: HttpStatusCode.NotFound);

        Assert.Null((await service.CheckAsync(CancellationToken.None)).Update);
    }

    [Fact]
    public async Task NetworkFailureIsSwallowed()
    {
        var handler = new ThrowingHandler();
        var service = new UpdateService(new HttpClient(handler), new FakeVersionProvider("1.0.0"));

        // A register with no internet must not see an error — it just keeps trading.
        Assert.Null((await service.CheckAsync(CancellationToken.None)).Update);
    }

    // ------------------------------------------------------------------ flavors
    //
    // Two builds ship per release: x64 for the fleet and x86 for the registers left on
    // 32-bit Windows 7. Each polls its own manifest, so neither can ever be handed the
    // other's installer.

    private const string X86ManifestUrl = "https://proffi.io/downloads/kassa-latest-x86.json";

    private static (UpdateService Service, StubHttpMessageHandler Handler) BuildFor(
        string manifestUrl, string body)
    {
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.OK, body));
        var service = new UpdateService(
            new HttpClient(handler),
            new FakeVersionProvider("1.0.0"),
            downloadDirectory: null,
            manifestUrl: manifestUrl);
        return (service, handler);
    }

    [Fact]
    public void DefaultManifestUrlFollowsTheProcessArchitecture()
    {
        // Both literals are a contract with release.ps1, which publishes exactly these
        // two file names. A register polling a name no release ever uploads reads the
        // 404 as "nothing new" and sits on an old build indefinitely, with nothing
        // anywhere reporting a fault.
        var expected = RuntimeInformation.ProcessArchitecture == Architecture.X86
            ? X86ManifestUrl
            : "https://proffi.io/downloads/kassa-latest.json";

        Assert.Equal(expected, UpdateService.DefaultManifestUrl);
    }

    [Fact]
    public async Task ThePollGoesToTheManifestItWasGiven()
    {
        var (service, handler) = BuildFor(X86ManifestUrl, Manifest());

        await service.CheckAsync(CancellationToken.None);

        Assert.Equal(X86ManifestUrl, handler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task DownloadHostIsPinnedToTheManifestActuallyPolled()
    {
        // Pinning follows this instance's manifest rather than a fixed constant. Had it
        // stayed bound to the x64 URL, moving the x86 manifest to another host would
        // quietly disable the check for those registers instead of failing loudly.
        var (service, _) = BuildFor(
            X86ManifestUrl,
            Manifest(url: "https://elsewhere.example/downloads/proffi-kassa-setup-x86.exe"));

        Assert.Null((await service.CheckAsync(CancellationToken.None)).Update);
    }

    [Fact]
    public async Task TheX86FlavorIsOfferedItsOwnInstaller()
    {
        var (service, _) = BuildFor(
            X86ManifestUrl,
            Manifest(url: "https://proffi.io/downloads/proffi-kassa-setup-x86.exe"));

        var info = (await service.CheckAsync(CancellationToken.None)).Update;

        Assert.NotNull(info);
        Assert.Equal("https://proffi.io/downloads/proffi-kassa-setup-x86.exe", info!.Url);
    }

    private static string Sha256Of(string content)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content)))
                      .ToLowerInvariant();
    }

    private static (UpdateService Service, string Directory) BuildForDownload(string payload)
    {
        var directory = Path.Combine(Path.GetTempPath(), "VvCashUpdateTest", Guid.NewGuid().ToString("N"));
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.OK, payload))
        {
            ContentType = "application/octet-stream"
        };
        var service = new UpdateService(
            new HttpClient(handler),
            new FakeVersionProvider("1.0.0"),
            directory);
        return (service, directory);
    }

    private static UpdateInfo InfoFor(string sha256, long size = 0)
        => new UpdateInfo(
            new Version(1, 1, 0),
            "https://proffi.io/downloads/proffi-kassa-setup.exe",
            sha256,
            size,
            null);

    [Fact]
    public async Task DownloadReturnsThePathWhenTheHashMatches()
    {
        const string payload = "pretend this is an installer";
        var (service, directory) = BuildForDownload(payload);
        try
        {
            var path = await service.DownloadAsync(
                InfoFor(Sha256Of(payload)), null, CancellationToken.None);

            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            Assert.Equal(payload, File.ReadAllText(path!));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadDeletesTheFileWhenTheHashDoesNotMatch()
    {
        var (service, directory) = BuildForDownload("tampered installer");
        try
        {
            // The hash of some other content: this is what a man-in-the-middle or a
            // truncated transfer looks like from the register's side.
            var path = await service.DownloadAsync(
                InfoFor(Sha256Of("the real installer")), null, CancellationToken.None);

            Assert.Null(path);
            // Nothing runnable may be left behind — a stale unverified installer on
            // disk is exactly what this check exists to prevent.
            Assert.Empty(Directory.Exists(directory) ? Directory.GetFiles(directory) : Array.Empty<string>());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadReportsProgress()
    {
        const string payload = "pretend this is an installer";
        var (service, directory) = BuildForDownload(payload);
        try
        {
            var reported = new System.Collections.Generic.List<double>();
            var path = await service.DownloadAsync(
                InfoFor(Sha256Of(payload), payload.Length),
                new Progress<double>(p => { lock (reported) reported.Add(p); }),
                CancellationToken.None);

            Assert.NotNull(path);
            // Progress<T> marshals through the synchronization context, so the exact
            // count is not deterministic; that the final value reached 1.0 is.
            await Task.Delay(50);
            lock (reported) Assert.Contains(reported, p => Math.Abs(p - 1.0) < 0.0001);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadReturnsNullOnHttpError()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VvCashUpdateTest", Guid.NewGuid().ToString("N"));
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.NotFound, "nope"));
        var service = new UpdateService(new HttpClient(handler), new FakeVersionProvider("1.0.0"), directory);
        try
        {
            Assert.Null(await service.DownloadAsync(InfoFor(ValidHash), null, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadProgressNeverExceedsOneWhenSizeIsUnderstated()
    {
        // A manifest sizeBytes that is stale/understated, combined with a response
        // that carries no Content-Length header (as a CDN answering chunked would):
        // total falls back to the understated sizeBytes below, so written/total can
        // climb past 1.0 without a clamp. An Avalonia ProgressBar bound above its
        // Maximum misbehaves.
        const string payload = "pretend this is an installer, a bit longer this time";
        var understatedSize = payload.Length / 2;
        var directory = Path.Combine(Path.GetTempPath(), "VvCashUpdateTest", Guid.NewGuid().ToString("N"));
        var handler = new NoContentLengthHandler(payload);
        var service = new UpdateService(new HttpClient(handler), new FakeVersionProvider("1.0.0"), directory);
        try
        {
            var reported = new System.Collections.Generic.List<double>();
            var path = await service.DownloadAsync(
                InfoFor(Sha256Of(payload), understatedSize),
                new Progress<double>(p => { lock (reported) reported.Add(p); }),
                CancellationToken.None);

            Assert.NotNull(path);
            await Task.Delay(50);
            lock (reported)
            {
                Assert.NotEmpty(reported);
                Assert.All(reported, p => Assert.True(p <= 1.0, $"progress {p} exceeded 1.0"));
                Assert.Contains(reported, p => Math.Abs(p - 1.0) < 0.0001);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => throw new HttpRequestException("no route to host");
    }

    /// <summary>Answers with a response carrying no Content-Length header at all, the
    /// way a chunked-transfer response from a CDN in front of proffi.io could. Plain
    /// StringContent (as used by <see cref="StubHttpMessageHandler"/>) always computes
    /// one, so this exists to force the manifest's sizeBytes fallback in
    /// DownloadAsync.</summary>
    private sealed class NoContentLengthHandler : HttpMessageHandler
    {
        private readonly string _payload;
        public NoContentLengthHandler(string payload) => _payload = payload;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new NoLengthContent(_payload) });

        private sealed class NoLengthContent : HttpContent
        {
            private readonly byte[] _bytes;
            public NoLengthContent(string content) => _bytes = System.Text.Encoding.UTF8.GetBytes(content);

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
                => stream.WriteAsync(_bytes, 0, _bytes.Length);

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }
    }
}
