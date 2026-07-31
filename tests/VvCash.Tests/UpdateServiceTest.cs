using System;
using System.Net;
using System.Net.Http;
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

        var info = await service.CheckAsync(CancellationToken.None);

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

        Assert.Null(await service.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OlderVersionIsNotOffered()
    {
        var (service, _) = Build(Manifest(version: "0.9.0"), currentVersion: "1.0.0");

        Assert.Null(await service.CheckAsync(CancellationToken.None));
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

        Assert.Null(await service.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ManifestForAnotherProductIsRejected()
    {
        var (service, _) = Build(Manifest(product: "softphone"));

        Assert.Null(await service.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PlainHttpUrlIsRejected()
    {
        var (service, _) = Build(Manifest(url: "http://proffi.io/downloads/proffi-kassa-setup.exe"));

        Assert.Null(await service.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RelativeUrlIsRejected()
    {
        var (service, _) = Build(Manifest(url: "/downloads/proffi-kassa-setup.exe"));

        Assert.Null(await service.CheckAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("deadbeef")]
    [InlineData("9f2b1c4a7e35d081bc6f42a90e5713d8cf20ab6749e83c15d02f7ba418c69e3")]
    [InlineData("zzzz1c4a7e35d081bc6f42a90e5713d8cf20ab6749e83c15d02f7ba418c69e3d")]
    public async Task MalformedHashIsRejected(string sha256)
    {
        var (service, _) = Build(Manifest(sha256: sha256));

        Assert.Null(await service.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UnparsableVersionIsRejected()
    {
        var (service, _) = Build(Manifest(version: "next-friday"));

        Assert.Null(await service.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BrokenJsonIsRejected()
    {
        var (service, _) = Build("{ \"product\": \"vvcash\", ");

        Assert.Null(await service.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NotFoundIsRejected()
    {
        var (service, _) = Build(Manifest(), status: HttpStatusCode.NotFound);

        Assert.Null(await service.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NetworkFailureIsSwallowed()
    {
        var handler = new ThrowingHandler();
        var service = new UpdateService(new HttpClient(handler), new FakeVersionProvider("1.0.0"));

        // A register with no internet must not see an error — it just keeps trading.
        Assert.Null(await service.CheckAsync(CancellationToken.None));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => throw new HttpRequestException("no route to host");
    }
}
