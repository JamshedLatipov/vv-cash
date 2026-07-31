# App Auto-Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The register discovers a newer build on `proffi.io`, shows the cashier a status-bar badge, and — on their command — downloads the same Inno installer, verifies its SHA-256 and reinstalls silently.

**Architecture:** A new `Services/Update` namespace holds four small units: `IAppVersionProvider` (what am I?), `IUpdateService` (what is published, and fetch it safely), `IInstallerLauncher` (start the installer process), and `UpdateViewModel` (all badge/modal state and commands). `PosViewModel` gains exactly one constructor parameter — the view model — and calls its `CheckAsync` once an hour from the background loop it already runs. The Inno pipeline is untouched apart from taking the version from the csproj and relaunching the app after a silent install.

**Tech Stack:** .NET 10, Avalonia 11.2.3, CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`), `System.Text.Json`, xUnit, Inno Setup 6, PowerShell.

**Spec:** [`docs/superpowers/specs/2026-07-31-app-auto-update-design.md`](../specs/2026-07-31-app-auto-update-design.md)

---

## File Structure

**Created:**

| Path | Responsibility |
|---|---|
| `src/VvCash/Services/Update/IAppVersionProvider.cs` | Interface + `AssemblyAppVersionProvider`. Reports the running build's version, normalised to three parts. |
| `src/VvCash/Services/Update/UpdateInfo.cs` | Immutable record describing one published release. |
| `src/VvCash/Services/Update/IUpdateService.cs` | Interface for check + download. |
| `src/VvCash/Services/Update/UpdateService.cs` | Manifest fetch, hard validation, download with SHA-256 verification. |
| `src/VvCash/Services/Update/IInstallerLauncher.cs` | Interface + `ProcessInstallerLauncher`. Isolates `Process.Start` so tests never run an installer. |
| `src/VvCash/ViewModels/UpdateViewModel.cs` | Badge visibility, modal state, download progress, error text, cart guard, commands. |
| `tests/VvCash.Tests/AppVersionProviderTest.cs` | Version normalisation. |
| `tests/VvCash.Tests/UpdateServiceTest.cs` | Manifest validation matrix and hash verification. |
| `tests/VvCash.Tests/UpdateViewModelTest.cs` | Cart guard, download outcomes, shutdown request. |

**Modified:**

| Path | Change |
|---|---|
| `src/VvCash/VvCash.csproj` | Add `<Version>`. |
| `build/installer/build_installer.ps1` | Read the version from the csproj, pass it to ISCC. |
| `build/installer/VvCashInstaller.iss` | Accept the version from the command line; add a silent-only `[Run]` entry. |
| `src/VvCash/App.axaml.cs` | Register the four new types; wire `ShutdownRequested`. |
| `src/VvCash/ViewModels/PosViewModel.cs` | One new constructor parameter, `Update` property, hourly check in the background loop. |
| `src/VvCash/Views/PosView.axaml` | Badge in the status bar, real version text, update modal. |
| `src/VvCash/Assets/i18n/{en,kk,ru,tg,uz}.json` | New keys. |
| `tests/VvCash.Tests/StubHttpMessageHandler.cs` | Settable response content type. |
| `tests/VvCash.Tests/PosViewModelSellerGateTest.cs` | Factory passes the new constructor argument. |

**Test command everywhere:** `& ./run-tests.ps1` — note the leading `&` and no `pwsh`; there is no `pwsh` on the build machine.

---

## Task 1: Version becomes a build property

The csproj becomes the single source of the version, the installer script reads it, and a silent install relaunches the app instead of leaving the register dark.

**Files:**
- Modify: `src/VvCash/VvCash.csproj`
- Modify: `build/installer/build_installer.ps1`
- Modify: `build/installer/VvCashInstaller.iss`

- [ ] **Step 1: Add the version to the csproj**

In `src/VvCash/VvCash.csproj`, inside the existing `<PropertyGroup>`, after the `<AssemblyName>VvCash</AssemblyName>` line:

```xml
    <AssemblyName>VvCash</AssemblyName>
    <!-- Single source of the product version: the installer script reads it from here
         and passes it to ISCC, and IAppVersionProvider reads it back off the built
         assembly at run time. Bump this line to cut a release. -->
    <Version>1.0.0</Version>
```

- [ ] **Step 2: Verify the version reaches the built assembly**

Run:

```bash
dotnet build src/VvCash/VvCash.csproj -c Release -o build/verify
```

Expected: build succeeds. Then run:

```bash
powershell -Command "(Get-Item build/verify/VvCash.dll).VersionInfo.FileVersion"
```

Expected output: `1.0.0.0`

(Build into `build/verify` on purpose — a running instance of the app locks the normal output directory.)

- [ ] **Step 3: Make the installer script pass the version to ISCC**

In `build/installer/build_installer.ps1`, after the line `$iss = Join-Path $PSScriptRoot 'VvCashInstaller.iss'`, add:

```powershell
$versionNode = ([xml](Get-Content $proj)).SelectSingleNode('//PropertyGroup/Version')
if (-not $versionNode) { throw "No <Version> element in $proj — the installer needs it." }
$version = $versionNode.InnerText.Trim()
Write-Host "==> Product version from csproj: $version" -ForegroundColor Cyan
```

Then replace the compile line:

```powershell
& $iscc $iss
```

with:

```powershell
& $iscc "/DAppVersion=$version" $iss
```

- [ ] **Step 4: Make the .iss accept that version and relaunch after a silent install**

In `build/installer/VvCashInstaller.iss`, replace this line:

```
#define AppVersion "1.0.0"
```

with:

```
; Version comes from build_installer.ps1 (/DAppVersion=...), which reads it out of
; VvCash.csproj. The fallback only exists so the script still compiles if someone runs
; ISCC by hand; a real release always gets the value passed in.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
```

Then replace the whole `[Run]` section:

```
[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
```

with:

```
[Run]
; Two entries on purpose. The first is the familiar "Run VvCash" checkbox on the last
; wizard page — postinstall makes it a checkbox, and skipifsilent means it does nothing
; during an unattended install.
;
; The second exists only for auto-update. The app downloads this installer and runs it
; with /VERYSILENT, so the entry above is skipped and the register would be left shut
; down with no way back. Check: WizardSilent is what keeps this entry out of a manual
; install — without it the cashier who installs by hand gets two copies of the app.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: WizardSilent
```

- [ ] **Step 5: Build the installer end to end**

Run:

```bash
powershell -ExecutionPolicy Bypass -File build/installer/build_installer.ps1
```

Expected: the script prints `==> Product version from csproj: 1.0.0`, then `==> Done. Installer: ...VvCashInstaller.exe (NN MB)`.

If Inno Setup 6 is not installed on the machine, this step cannot run — record that and move on; nothing later in the plan depends on it.

- [ ] **Step 6: Commit**

```bash
git add src/VvCash/VvCash.csproj build/installer/build_installer.ps1 build/installer/VvCashInstaller.iss
git commit -m "build: take the installer version from the csproj and relaunch after a silent install"
```

---

## Task 2: The app learns its own version

**Files:**
- Create: `src/VvCash/Services/Update/IAppVersionProvider.cs`
- Test: `tests/VvCash.Tests/AppVersionProviderTest.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/VvCash.Tests/AppVersionProviderTest.cs`:

```csharp
using System;
using VvCash.Services.Update;
using Xunit;

namespace VvCash.Tests;

public class AppVersionProviderTest
{
    [Theory]
    [InlineData(1, 0, 0, 0, "1.0.0")]
    [InlineData(1, 2, 3, 4, "1.2.3")]
    [InlineData(2, 5, -1, -1, "2.5.0")]
    public void NormalizeTrimsToThreeParts(int major, int minor, int build, int revision, string expected)
    {
        var raw = revision >= 0
            ? new Version(major, minor, build, revision)
            : build >= 0 ? new Version(major, minor, build) : new Version(major, minor);

        Assert.Equal(expected, AppVersion.Normalize(raw).ToString());
    }

    [Fact]
    public void AssemblyProviderReportsAThreePartVersion()
    {
        var provider = new AssemblyAppVersionProvider();

        // Whatever the test host reports, the provider must hand back exactly three
        // components — everything downstream formats and compares on that assumption.
        Assert.True(provider.Current.Build >= 0);
        Assert.Equal(-1, provider.Current.Revision);
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `& ./run-tests.ps1 --filter AppVersionProviderTest`

Expected: compile error — `AppVersion` and `AssemblyAppVersionProvider` do not exist.

- [ ] **Step 3: Write the implementation**

Create `src/VvCash/Services/Update/IAppVersionProvider.cs`:

```csharp
using System;
using System.Reflection;

namespace VvCash.Services.Update;

/// <summary>The running build's version. An interface with one property looks like
/// ceremony, but the update check is entirely a comparison against this value, so
/// without a seam here no test can describe "the register is older than what the
/// server publishes" without rebuilding the assembly.</summary>
public interface IAppVersionProvider
{
    Version Current { get; }
}

public static class AppVersion
{
    /// <summary>Trims a version to Major.Minor.Build.
    ///
    /// Both sides of the update comparison need this. An assembly version always has
    /// four components (1.0.0 in the csproj builds as 1.0.0.0), while a hand-written
    /// manifest says "1.0.0" and parses with Revision = -1. System.Version compares
    /// missing components as -1, so the unnormalised pair 1.0.0.0 and 1.0.0 are *not*
    /// equal — the running build would read as newer than the release it came from,
    /// and an update would never be offered. Build is clamped rather than passed
    /// through because "1.1" parses with Build = -1, and the Version constructor
    /// rejects a negative component outright.</summary>
    public static Version Normalize(Version version)
        => new Version(version.Major, version.Minor, version.Build < 0 ? 0 : version.Build);
}

public sealed class AssemblyAppVersionProvider : IAppVersionProvider
{
    public Version Current { get; } = AppVersion.Normalize(
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));
}
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `& ./run-tests.ps1 --filter AppVersionProviderTest`

Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Services/Update/IAppVersionProvider.cs tests/VvCash.Tests/AppVersionProviderTest.cs
git commit -m "feat(update): report the running build's version"
```

---

## Task 3: The stub handler can return a content type

`UpdateService` rejects anything that is not `application/json`, and the single most important test in this feature is the one where `proffi.io` answers a missing file with an HTML page under status 200. The existing stub hardcodes `application/json`, so that case cannot be written until this changes.

**Files:**
- Modify: `tests/VvCash.Tests/StubHttpMessageHandler.cs`

- [ ] **Step 1: Add a settable content type**

In `tests/VvCash.Tests/StubHttpMessageHandler.cs`, add the property after `LastRequestBody`:

```csharp
    public string? LastRequestBody { get; private set; }

    /// <summary>Content type of every stubbed response. Defaults to JSON, which is what
    /// every existing caller assumed. UpdateServiceTest overrides it to reproduce the
    /// SPA fallback on proffi.io, which answers a missing path with text/html under
    /// status 200.</summary>
    public string ContentType { get; set; } = "application/json";
```

Then change the response construction from:

```csharp
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
```

to:

```csharp
            Content = new StringContent(body, System.Text.Encoding.UTF8, ContentType)
```

- [ ] **Step 2: Confirm nothing else broke**

Run: `& ./run-tests.ps1`

Expected: the suite passes. Two tests are known to fail only on full runs and pass in isolation — if exactly those two fail, re-run them alone before suspecting this change.

- [ ] **Step 3: Commit**

```bash
git add tests/VvCash.Tests/StubHttpMessageHandler.cs
git commit -m "test: let the stub handler answer with a chosen content type"
```

---

## Task 4: Manifest fetch and validation

**Files:**
- Create: `src/VvCash/Services/Update/UpdateInfo.cs`
- Create: `src/VvCash/Services/Update/IUpdateService.cs`
- Create: `src/VvCash/Services/Update/UpdateService.cs`
- Test: `tests/VvCash.Tests/UpdateServiceTest.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/VvCash.Tests/UpdateServiceTest.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `& ./run-tests.ps1 --filter UpdateServiceTest`

Expected: compile error — `UpdateService` and `UpdateInfo` do not exist.

- [ ] **Step 3: Write the record**

Create `src/VvCash/Services/Update/UpdateInfo.cs`:

```csharp
using System;

namespace VvCash.Services.Update;

/// <summary>One published release, after the manifest has passed validation. Every
/// field here has already been checked: Version is normalised to three parts, Url is
/// absolute and https, Sha256 is 64 lowercase hex characters.</summary>
public sealed record UpdateInfo(
    Version Version,
    string Url,
    string Sha256,
    long SizeBytes,
    string? Notes);
```

- [ ] **Step 4: Write the interface**

Create `src/VvCash/Services/Update/IUpdateService.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VvCash.Services.Update;

public interface IUpdateService
{
    /// <summary>Fetches and validates the manifest. Returns null both when there is no
    /// newer release and when anything at all went wrong — a caller has nothing useful
    /// to do with the difference, and the register must not nag the cashier about the
    /// update server.</summary>
    Task<UpdateInfo?> CheckAsync(CancellationToken ct);

    /// <summary>Downloads the installer and verifies its SHA-256. Returns the path to
    /// the verified file, or null if the download failed, was cancelled, or the hash
    /// did not match. Never returns a path to an unverified file.</summary>
    Task<string?> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct);
}
```

- [ ] **Step 5: Write the implementation (check only for now)**

Create `src/VvCash/Services/Update/UpdateService.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VvCash.Services.Update;

public sealed class UpdateService : IUpdateService
{
    /// <summary>Published next to the installer and uploaded by the same hand. Not a
    /// setting: every register talks to the cloud, and a per-register update URL would
    /// be one more thing to get wrong on site.</summary>
    private const string ManifestUrl = "https://proffi.io/downloads/kassa-latest.json";

    private const string ProductId = "vvcash";

    private readonly HttpClient _httpClient;
    private readonly IAppVersionProvider _versionProvider;
    private readonly string _downloadDirectory;

    public UpdateService(
        HttpClient httpClient,
        IAppVersionProvider versionProvider,
        string? downloadDirectory = null)
    {
        _httpClient = httpClient;
        _versionProvider = versionProvider;
        _downloadDirectory = downloadDirectory
            ?? Path.Combine(Path.GetTempPath(), "VvCash", "updates");
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await _httpClient.GetAsync(ManifestUrl, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;

            // proffi.io serves a single-page app: a path it does not know answers 200
            // with index.html. The status code alone proves nothing.
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                return null;

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var info = Parse(body);
            if (info is null) return null;

            return info.Version > AppVersion.Normalize(_versionProvider.Current) ? info : null;
        }
        catch
        {
            // No network, DNS failure, timeout, torn connection. All the same to the
            // cashier: nothing appears, and the loop tries again in an hour.
            return null;
        }
    }

    internal static UpdateInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!TryGetString(root, "product", out var product) || product != ProductId) return null;

            if (!TryGetString(root, "version", out var versionText)) return null;
            if (!Version.TryParse(versionText, out var version)) return null;

            if (!TryGetString(root, "url", out var url)) return null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttps) return null;

            if (!TryGetString(root, "sha256", out var sha256)) return null;
            if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) return null;

            long sizeBytes = root.TryGetProperty("sizeBytes", out var sizeElement)
                             && sizeElement.ValueKind == JsonValueKind.Number
                             && sizeElement.TryGetInt64(out var size)
                ? size
                : 0;

            string? notes = TryGetString(root, "notes", out var notesText) ? notesText : null;

            return new UpdateInfo(
                AppVersion.Normalize(version),
                uri.ToString(),
                sha256.ToLowerInvariant(),
                sizeBytes,
                notes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element)) return false;
        if (element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? string.Empty;
        return true;
    }

    public Task<string?> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
        => throw new NotImplementedException("Task 5");
}
```

- [ ] **Step 6: Run the tests and watch them pass**

Run: `& ./run-tests.ps1 --filter UpdateServiceTest`

Expected: PASS, 15 tests.

- [ ] **Step 7: Commit**

```bash
git add src/VvCash/Services/Update tests/VvCash.Tests/UpdateServiceTest.cs
git commit -m "feat(update): fetch and validate the release manifest"
```

---

## Task 5: Download and hash verification

**Files:**
- Modify: `src/VvCash/Services/Update/UpdateService.cs`
- Test: `tests/VvCash.Tests/UpdateServiceTest.cs`

- [ ] **Step 1: Write the failing tests**

Append to the `UpdateServiceTest` class in `tests/VvCash.Tests/UpdateServiceTest.cs`, before the private `ThrowingHandler` class:

```csharp
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
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `& ./run-tests.ps1 --filter UpdateServiceTest`

Expected: the four new tests fail with `NotImplementedException: Task 5`.

- [ ] **Step 3: Write the implementation**

In `src/VvCash/Services/Update/UpdateService.cs`, replace the throwing `DownloadAsync` with:

```csharp
    public async Task<string?> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        // Clear leftovers here rather than in CheckAsync: the check runs on a timer, and
        // clearing there could delete a download the cashier started minutes ago.
        ClearDownloadDirectory();
        Directory.CreateDirectory(_downloadDirectory);

        var target = Path.Combine(_downloadDirectory, "VvCashInstaller.exe");
        try
        {
            using (var response = await _httpClient.GetAsync(
                       info.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode) return null;

                var total = response.Content.Headers.ContentLength ?? info.SizeBytes;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var destination = File.Create(target);

                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                    if (total > 0) progress?.Report((double)written / total);
                }
            }

            if (!await HashMatchesAsync(target, info.Sha256, ct))
            {
                TryDelete(target);
                return null;
            }

            return target;
        }
        catch
        {
            // Cancelled, connection dropped, disk full. A partially written installer
            // must never survive — the next attempt starts clean.
            TryDelete(target);
            return null;
        }
    }

    private static async Task<bool> HashMatchesAsync(string path, string expected, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return string.Equals(Convert.ToHexString(hash), expected, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearDownloadDirectory()
    {
        try
        {
            if (Directory.Exists(_downloadDirectory)) Directory.Delete(_downloadDirectory, recursive: true);
        }
        catch
        {
            // A file still held open by a previous run is not worth failing over; the
            // download below overwrites what it needs.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort. The hash check is the guard that matters, and it already said no.
        }
    }
```

- [ ] **Step 4: Run the tests and watch them pass**

Run: `& ./run-tests.ps1 --filter UpdateServiceTest`

Expected: PASS, 19 tests.

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Services/Update/UpdateService.cs tests/VvCash.Tests/UpdateServiceTest.cs
git commit -m "feat(update): download the installer and verify its hash before trusting it"
```

---

## Task 6: Launching the installer

No test: the whole point of this type is that it is the one place with a side effect worth isolating, and the tests replace it.

**Files:**
- Create: `src/VvCash/Services/Update/IInstallerLauncher.cs`

- [ ] **Step 1: Write the interface and implementation**

Create `src/VvCash/Services/Update/IInstallerLauncher.cs`:

```csharp
using System.Diagnostics;

namespace VvCash.Services.Update;

/// <summary>Starts the downloaded installer. Exists as an interface so no test ever
/// launches a real installer — every other part of the update flow is worth testing,
/// and this is the one call that cannot be.</summary>
public interface IInstallerLauncher
{
    /// <summary>Starts the installer unattended. Throws if the process cannot start.</summary>
    void Launch(string installerPath);
}

public sealed class ProcessInstallerLauncher : IInstallerLauncher
{
    public void Launch(string installerPath)
    {
        // /VERYSILENT   — no wizard at all; the cashier already agreed in our own dialog.
        // /SUPPRESSMSGBOXES — nothing may wait for a click once the app has exited.
        // /NORESTART    — never reboot the till, whatever the installer thinks it needs.
        //
        // The child process outlives this one on purpose: the caller shuts the app down
        // immediately afterwards, because Inno cannot overwrite a running VvCash.exe.
        var startInfo = new ProcessStartInfo(installerPath)
        {
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = true
        };
        Process.Start(startInfo);
    }
}
```

- [ ] **Step 2: Confirm it compiles**

Run: `dotnet build src/VvCash/VvCash.csproj -c Debug -o build/verify`

Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/VvCash/Services/Update/IInstallerLauncher.cs
git commit -m "feat(update): isolate the installer launch behind an interface"
```

---

## Task 7: The update view model

Holds every piece of state the screen binds to, so `PosViewModel` gains one member instead of seven.

**Files:**
- Create: `src/VvCash/ViewModels/UpdateViewModel.cs`
- Test: `tests/VvCash.Tests/UpdateViewModelTest.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/VvCash.Tests/UpdateViewModelTest.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Update;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

public class UpdateViewModelTest
{
    private sealed class FakeVersionProvider : IAppVersionProvider
    {
        public Version Current { get; set; } = new Version(1, 0, 0);
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        public UpdateInfo? Available;
        public string? DownloadResult;
        public int DownloadCalls;

        public Task<UpdateInfo?> CheckAsync(CancellationToken ct) => Task.FromResult(Available);

        public Task<string?> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
        {
            DownloadCalls++;
            progress?.Report(1.0);
            return Task.FromResult(DownloadResult);
        }
    }

    private sealed class FakeLauncher : IInstallerLauncher
    {
        public string? Launched;
        public Exception? Throw;

        public void Launch(string installerPath)
        {
            if (Throw is not null) throw Throw;
            Launched = installerPath;
        }
    }

    private static UpdateInfo SampleInfo() => new UpdateInfo(
        new Version(1, 1, 0),
        "https://proffi.io/downloads/proffi-kassa-setup.exe",
        "9f2b1c4a7e35d081bc6f42a90e5713d8cf20ab6749e83c15d02f7ba418c69e3d",
        35651584,
        "test build");

    private static (UpdateViewModel Vm, FakeUpdateService Service, FakeLauncher Launcher, CartService Cart)
        Build()
    {
        // StubPromotionProvider lives in tests/VvCash.Tests/StubPromotionProvider.cs and
        // is how every other test builds a real CartService without touching SQLite.
        var cart = new CartService(new StubPromotionProvider());
        var service = new FakeUpdateService();
        var launcher = new FakeLauncher();
        var vm = new UpdateViewModel(service, launcher, cart, new FakeVersionProvider());
        return (vm, service, launcher, cart);
    }

    private static Product SampleProduct() => new Product
    {
        Id = "p1",
        Name = "Product 1",
        Sku = "p1",
        Price = 10m
    };

    [Fact]
    public void VersionTextShowsTheRunningBuild()
    {
        var (vm, _, _, _) = Build();

        Assert.Equal("V 1.0.0", vm.AppVersionText);
    }

    [Fact]
    public async Task CheckRaisesTheBadgeWhenAReleaseIsAvailable()
    {
        var (vm, service, _, _) = Build();
        service.Available = SampleInfo();

        await vm.CheckAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsUpdateAvailable);
        Assert.Equal(new Version(1, 1, 0), vm.AvailableUpdate!.Version);
    }

    [Fact]
    public async Task CheckLeavesTheBadgeHiddenWhenThereIsNothingNew()
    {
        var (vm, service, _, _) = Build();
        service.Available = null;

        await vm.CheckAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsUpdateAvailable);
    }

    [Fact]
    public void InstallIsBlockedWhileTheCartHasItems()
    {
        var (vm, _, _, cart) = Build();

        Assert.True(vm.CanInstall);

        cart.AddProduct(SampleProduct());

        // Restarting the register mid-receipt would lose the sale in progress.
        Assert.False(vm.CanInstall);

        cart.ClearCart();

        Assert.True(vm.CanInstall);
    }

    [Fact]
    public async Task StartUpdateRefusesWhileTheCartHasItems()
    {
        var (vm, service, launcher, cart) = Build();
        service.Available = SampleInfo();
        service.DownloadResult = @"C:\temp\VvCashInstaller.exe";
        await vm.CheckAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        cart.AddProduct(SampleProduct());
        await vm.StartUpdateCommand.ExecuteAsync(null);

        Assert.Equal(0, service.DownloadCalls);
        Assert.Null(launcher.Launched);
    }

    [Fact]
    public async Task SuccessfulUpdateLaunchesTheInstallerAndAsksForShutdown()
    {
        var (vm, service, launcher, _) = Build();
        service.Available = SampleInfo();
        service.DownloadResult = @"C:\temp\VvCashInstaller.exe";
        var shutdownRequested = false;
        vm.ShutdownRequested = () => shutdownRequested = true;

        await vm.CheckAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        await vm.StartUpdateCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\temp\VvCashInstaller.exe", launcher.Launched);
        Assert.True(shutdownRequested);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public async Task FailedDownloadShowsAnErrorAndDoesNotShutDown()
    {
        var (vm, service, launcher, _) = Build();
        service.Available = SampleInfo();
        service.DownloadResult = null;
        var shutdownRequested = false;
        vm.ShutdownRequested = () => shutdownRequested = true;

        await vm.CheckAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        await vm.StartUpdateCommand.ExecuteAsync(null);

        Assert.Null(launcher.Launched);
        Assert.False(shutdownRequested);
        Assert.False(string.IsNullOrEmpty(vm.ErrorText));
        Assert.False(vm.IsDownloading);
    }

    [Fact]
    public async Task FailedLaunchShowsThePathSoItCanBeRunByHand()
    {
        var (vm, service, launcher, _) = Build();
        service.Available = SampleInfo();
        service.DownloadResult = @"C:\temp\VvCashInstaller.exe";
        launcher.Throw = new System.ComponentModel.Win32Exception("access denied");
        var shutdownRequested = false;
        vm.ShutdownRequested = () => shutdownRequested = true;

        await vm.CheckAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        await vm.StartUpdateCommand.ExecuteAsync(null);

        Assert.False(shutdownRequested);
        Assert.Contains(@"C:\temp\VvCashInstaller.exe", vm.ErrorText);
    }

    [Fact]
    public async Task DismissHidesTheModalButKeepsTheBadge()
    {
        var (vm, service, _, _) = Build();
        service.Available = SampleInfo();
        await vm.CheckAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        vm.OpenModalCommand.Execute(null);
        Assert.True(vm.IsModalVisible);

        vm.CloseModalCommand.Execute(null);

        // "Later" is not "never" — the badge stays so the cashier can come back after
        // closing the shift.
        Assert.False(vm.IsModalVisible);
        Assert.True(vm.IsUpdateAvailable);
    }
}
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `& ./run-tests.ps1 --filter UpdateViewModelTest`

Expected: compile error — `UpdateViewModel` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/VvCash/ViewModels/UpdateViewModel.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Services;
using VvCash.Services.Update;

namespace VvCash.ViewModels;

public partial class UpdateViewModel : ViewModelBase
{
    private readonly IUpdateService _updateService;
    private readonly IInstallerLauncher _launcher;
    private readonly ICartService _cartService;
    private CancellationTokenSource? _downloadCts;

    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private bool _isModalVisible;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private UpdateInfo? _availableUpdate;

    /// <summary>The running build, formatted for the status bar.</summary>
    public string AppVersionText { get; }

    /// <summary>Set by App.axaml.cs to shut the desktop lifetime down. A settable
    /// delegate rather than a direct call, matching PosViewModel.NavigationRequest:
    /// the view model states intent, the host decides how it happens, and a test can
    /// observe it without an application.</summary>
    public Action? ShutdownRequested { get; set; }

    /// <summary>False while a receipt is in progress. Inno replaces the running exe, so
    /// installing mid-sale means losing whatever the cashier has rung up.</summary>
    public bool CanInstall => _cartService.Items.Count == 0 && !IsDownloading;

    public UpdateViewModel(
        IUpdateService updateService,
        IInstallerLauncher launcher,
        ICartService cartService,
        IAppVersionProvider versionProvider)
    {
        _updateService = updateService;
        _launcher = launcher;
        _cartService = cartService;

        AppVersionText = $"V {versionProvider.Current}";
        _cartService.CartChanged += (_, _) => OnPropertyChanged(nameof(CanInstall));
    }

    partial void OnIsDownloadingChanged(bool value) => OnPropertyChanged(nameof(CanInstall));

    /// <summary>Called from PosViewModel's background loop. Never throws — the service
    /// already swallows every failure and answers null.</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        var info = await _updateService.CheckAsync(ct);
        if (info is null) return;

        // The loop runs on a background thread (Task.Run, no captured UI context), and
        // these two properties are bound. Same idiom as PosViewModel's own
        // IsSystemOnline hand-off.
        Dispatcher.UIThread.Post(() =>
        {
            AvailableUpdate = info;
            IsUpdateAvailable = true;
        });
    }

    [RelayCommand]
    private void OpenModal()
    {
        ErrorText = null;
        IsModalVisible = true;
    }

    [RelayCommand]
    private void CloseModal() => IsModalVisible = false;

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    [RelayCommand]
    private async Task StartUpdateAsync()
    {
        if (AvailableUpdate is null || !CanInstall) return;

        ErrorText = null;
        DownloadProgress = 0;
        IsDownloading = true;
        _downloadCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<double>(value => DownloadProgress = value);
            var path = await _updateService.DownloadAsync(AvailableUpdate, progress, _downloadCts.Token);

            if (path is null)
            {
                ErrorText = I18nService.Instance["UpdateDownloadFailed"];
                return;
            }

            try
            {
                _launcher.Launch(path);
            }
            catch (Exception)
            {
                // The file is downloaded and verified — it just could not be started.
                // Show where it is so someone can double-click it.
                ErrorText = $"{I18nService.Instance["UpdateLaunchFailed"]} {path}";
                return;
            }

            // Inno cannot overwrite a running VvCash.exe, so the app has to get out of
            // the way. The installer's silent [Run] entry brings it back.
            ShutdownRequested?.Invoke();
        }
        finally
        {
            IsDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }
}
```

- [ ] **Step 4: Run the tests and watch them pass**

Run: `& ./run-tests.ps1 --filter UpdateViewModelTest`

Expected: PASS, 9 tests. `I18nService.Instance["UpdateDownloadFailed"]` returns the key name itself until Task 9 adds translations, which is enough for the assertions here (they only check the text is non-empty and contains the path).

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/ViewModels/UpdateViewModel.cs tests/VvCash.Tests/UpdateViewModelTest.cs
git commit -m "feat(update): hold badge, modal and install state in a view model"
```

---

## Task 8: Wiring — DI and the hourly check

**Files:**
- Modify: `src/VvCash/App.axaml.cs`
- Modify: `src/VvCash/ViewModels/PosViewModel.cs`
- Modify: `tests/VvCash.Tests/PosViewModelSellerGateTest.cs`

- [ ] **Step 1: Register the services**

In `src/VvCash/App.axaml.cs`, in the DI method, add after the line registering `ISyncService`:

```csharp
        services.AddHttpClient<ISyncService, SyncService>().AddHttpMessageHandler<AuthHeaderHandler>();

        // Update services. Note the missing AddHttpMessageHandler<AuthHeaderHandler>():
        // every other client here talks to the register's own backend, but this one
        // talks to proffi.io, and the register's bearer token has no business going
        // to a host that is not our API.
        services.AddSingleton<IAppVersionProvider, AssemblyAppVersionProvider>();
        services.AddSingleton<IInstallerLauncher, ProcessInstallerLauncher>();
        services.AddHttpClient<IUpdateService, UpdateService>();
```

Add the view model registration next to `services.AddSingleton<MainViewModel>();`:

```csharp
        // Singleton, unlike PosViewModel: a discovered update must survive navigation,
        // otherwise the badge disappears the moment the cashier opens returns.
        services.AddSingleton<UpdateViewModel>();
        services.AddSingleton<MainViewModel>();
```

Add the using at the top of the file, alongside the other `VvCash.Services.*` usings:

```csharp
using VvCash.Services.Update;
```

- [ ] **Step 2: Wire the shutdown delegate**

Still in `src/VvCash/App.axaml.cs`, in `OnFrameworkInitializationCompleted`, immediately after the desktop lifetime and service provider both exist and before the main window is shown, add:

```csharp
            // The view model asks; the host decides how. Shutdown() rather than
            // MainWindow.Close() because the installer is already running and every
            // window has to go, not just the main one.
            var updateViewModel = _serviceProvider.GetRequiredService<UpdateViewModel>();
            updateViewModel.ShutdownRequested = () => desktop.Shutdown();
```

If the local variables in that method are named differently, use the existing names — the requirement is only that this runs once at startup, after the provider is built.

- [ ] **Step 3: Add the constructor parameter to PosViewModel**

In `src/VvCash/ViewModels/PosViewModel.cs`, add the parameter at the end of the constructor's parameter list:

```csharp
        IAuthService authService,
        ICashFeatureService features,
        UpdateViewModel update)
```

and assign it in the body next to the other assignments:

```csharp
        _features = features;
        Update = update;
```

Add the property near `SellerSwitchViewModel`:

```csharp
    /// <summary>Update badge and modal state. Injected rather than built here because it
    /// is a singleton: PosViewModel is transient, and an update found before the cashier
    /// visited returns must still be on screen when they come back.</summary>
    public UpdateViewModel Update { get; }
```

Add the using at the top of the file:

```csharp
using VvCash.Services.Update;
```

- [ ] **Step 4: Check for updates once an hour in the existing loop**

In `StartBackgroundSync`, add a second timestamp next to `lastSyncTime`:

```csharp
            DateTime lastSyncTime = DateTime.MinValue;

            // Deliberately not MinValue: the first check waits a minute so it does not
            // compete with login and the first catalogue sync for the same connection.
            DateTime lastUpdateCheck = DateTime.Now - TimeSpan.FromMinutes(59);
```

Then, after the `if (DateTime.Now - lastSyncTime >= ...)` block closes and before the `try { await Task.Delay(...) }`, add:

```csharp
                // Once an hour is plenty: releases are cut by hand, and the register
                // stays on all day. CheckAsync never throws and marshals its own state
                // changes to the UI thread.
                if (DateTime.Now - lastUpdateCheck >= TimeSpan.FromHours(1))
                {
                    lastUpdateCheck = DateTime.Now;
                    await Update.CheckAsync(token);
                }
```

- [ ] **Step 5: Update the test factory**

In `tests/VvCash.Tests/PosViewModelSellerGateTest.cs`, find the `CreateViewModel` helper and add the new argument at the end of the `new PosViewModel(...)` call:

```csharp
            deps.AuthService,
            deps.Features,
            new UpdateViewModel(
                new NoUpdateService(),
                new NoInstallerLauncher(),
                deps.CartService,
                new FixedVersionProvider()));
```

Add these three fakes as private nested classes in the same test class:

```csharp
    private sealed class NoUpdateService : VvCash.Services.Update.IUpdateService
    {
        public Task<VvCash.Services.Update.UpdateInfo?> CheckAsync(CancellationToken ct)
            => Task.FromResult<VvCash.Services.Update.UpdateInfo?>(null);

        public Task<string?> DownloadAsync(
            VvCash.Services.Update.UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }

    private sealed class NoInstallerLauncher : VvCash.Services.Update.IInstallerLauncher
    {
        public void Launch(string installerPath) { }
    }

    private sealed class FixedVersionProvider : VvCash.Services.Update.IAppVersionProvider
    {
        public Version Current { get; } = new Version(1, 0, 0);
    }
```

- [ ] **Step 6: Run the whole suite**

Run: `& ./run-tests.ps1`

Expected: the suite passes. Two tests are known to fail only on full runs and pass in isolation — re-run those two alone before suspecting this change.

- [ ] **Step 7: Commit**

```bash
git add src/VvCash/App.axaml.cs src/VvCash/ViewModels/PosViewModel.cs tests/VvCash.Tests/PosViewModelSellerGateTest.cs
git commit -m "feat(update): check for a new release hourly from the background loop"
```

---

## Task 9: Translations

Nine keys in five files. Done before the XAML so the screen has something to show.

**Files:**
- Modify: `src/VvCash/Assets/i18n/ru.json`
- Modify: `src/VvCash/Assets/i18n/en.json`
- Modify: `src/VvCash/Assets/i18n/kk.json`
- Modify: `src/VvCash/Assets/i18n/tg.json`
- Modify: `src/VvCash/Assets/i18n/uz.json`

- [ ] **Step 1: Add the keys**

Add these entries to each file, before the closing brace. Note that `UpdateAvailableBadge` and `UpdateVersionLine` are used with `StringFormat`, so each must keep exactly one `{0}`.

`ru.json`:

```json
  "UpdateAvailableBadge": "ДОСТУПНО ОБНОВЛЕНИЕ",
  "UpdateModalTitle": "Обновление кассы",
  "UpdateVersionLine": "Новая версия {0}",
  "UpdateInstallButton": "Обновить",
  "UpdateLaterButton": "Позже",
  "UpdateDownloading": "Загрузка обновления...",
  "UpdateBlockedByCart": "Завершите текущий чек",
  "UpdateDownloadFailed": "Не удалось загрузить обновление. Попробуйте позже.",
  "UpdateLaunchFailed": "Не удалось запустить установку. Запустите файл вручную:"
```

`en.json`:

```json
  "UpdateAvailableBadge": "UPDATE AVAILABLE",
  "UpdateModalTitle": "Register update",
  "UpdateVersionLine": "New version {0}",
  "UpdateInstallButton": "Update",
  "UpdateLaterButton": "Later",
  "UpdateDownloading": "Downloading update...",
  "UpdateBlockedByCart": "Finish the current receipt first",
  "UpdateDownloadFailed": "Could not download the update. Try again later.",
  "UpdateLaunchFailed": "Could not start the installer. Run this file by hand:"
```

`kk.json`:

```json
  "UpdateAvailableBadge": "ЖАҢАРТУ БАР",
  "UpdateModalTitle": "Касса жаңартуы",
  "UpdateVersionLine": "Жаңа нұсқа {0}",
  "UpdateInstallButton": "Жаңарту",
  "UpdateLaterButton": "Кейінірек",
  "UpdateDownloading": "Жаңарту жүктелуде...",
  "UpdateBlockedByCart": "Алдымен ағымдағы чекті аяқтаңыз",
  "UpdateDownloadFailed": "Жаңартуды жүктеу мүмкін болмады. Кейінірек қайталаңыз.",
  "UpdateLaunchFailed": "Орнатуды бастау мүмкін болмады. Файлды қолмен іске қосыңыз:"
```

`tg.json`:

```json
  "UpdateAvailableBadge": "НАВСОЗӢ ДАСТРАС АСТ",
  "UpdateModalTitle": "Навсозии касса",
  "UpdateVersionLine": "Нусхаи нав {0}",
  "UpdateInstallButton": "Навсозӣ",
  "UpdateLaterButton": "Баъдтар",
  "UpdateDownloading": "Боргирии навсозӣ...",
  "UpdateBlockedByCart": "Аввал чеки ҷориро анҷом диҳед",
  "UpdateDownloadFailed": "Навсозиро боргирӣ карда нашуд. Баъдтар кӯшиш кунед.",
  "UpdateLaunchFailed": "Насбкуниро оғоз карда нашуд. Файлро дастӣ оғоз кунед:"
```

`uz.json`:

```json
  "UpdateAvailableBadge": "YANGILANISH MAVJUD",
  "UpdateModalTitle": "Kassa yangilanishi",
  "UpdateVersionLine": "Yangi versiya {0}",
  "UpdateInstallButton": "Yangilash",
  "UpdateLaterButton": "Keyinroq",
  "UpdateDownloading": "Yangilanish yuklanmoqda...",
  "UpdateBlockedByCart": "Avval joriy chekni yakunlang",
  "UpdateDownloadFailed": "Yangilanishni yuklab bo'lmadi. Keyinroq urinib ko'ring.",
  "UpdateLaunchFailed": "O'rnatishni boshlab bo'lmadi. Faylni qo'lda ishga tushiring:"
```

- [ ] **Step 2: Verify every file is still valid JSON**

Run:

```bash
node -e "['en','kk','ru','tg','uz'].forEach(l=>{JSON.parse(require('fs').readFileSync('src/VvCash/Assets/i18n/'+l+'.json','utf8'));console.log(l+' ok')})"
```

Expected: five `ok` lines. If node is unavailable, run `& ./run-tests.ps1 --filter SmokeTest` instead — the app loads these files at startup.

- [ ] **Step 3: Commit**

```bash
git add src/VvCash/Assets/i18n
git commit -m "i18n(update): add update badge and dialog strings"
```

---

## Task 10: The screen

**Files:**
- Modify: `src/VvCash/Views/PosView.axaml`

- [ ] **Step 1: Add the badge to the status bar**

In `src/VvCash/Views/PosView.axaml`, in the status bar's first `StackPanel` (the one holding the online, printer and unsynced indicators), add a fourth entry after the `HasUnsyncedDocuments` panel:

```xml
                    <Button Classes="IconButton" Padding="0" Background="Transparent" BorderThickness="0"
                            IsVisible="{Binding Update.IsUpdateAvailable}"
                            Command="{Binding Update.OpenModalCommand}">
                        <StackPanel Orientation="Horizontal" Spacing="4">
                            <material:MaterialIcon Kind="ArrowUpBoldCircleOutline" Width="14" Height="14" Foreground="#16a34a"/>
                            <TextBlock Text="{Binding [UpdateAvailableBadge], Source={x:Static services:I18nService.Instance}}"
                                       Foreground="#16a34a" FontSize="10" FontWeight="Bold" LetterSpacing="1"/>
                        </StackPanel>
                    </Button>
```

- [ ] **Step 2: Show the real version instead of the mockup string**

In the same status bar, replace the last `TextBlock` in column 2:

```xml
                <TextBlock Grid.Column="2" Text="{Binding [V240TERMINALIDLXP099], Source={x:Static services:I18nService.Instance}}" Foreground="{StaticResource Slate400Brush}" FontSize="10" FontWeight="Bold" LetterSpacing="1" VerticalAlignment="Center"/>
```

with:

```xml
                <!-- Was the i18n key V240TERMINALIDLXP099, a leftover from the mockup that
                     read "V 2.4.0 • TERMINAL ID: LXP-09921" on every register regardless of
                     what was installed. Now the actual build. -->
                <TextBlock Grid.Column="2" Text="{Binding Update.AppVersionText}" Foreground="{StaticResource Slate400Brush}" FontSize="10" FontWeight="Bold" LetterSpacing="1" VerticalAlignment="Center"/>
```

- [ ] **Step 3: Add the update modal**

In the `<!-- ======================= Modals ======================= -->` section, after the existing modals, add:

```xml
        <!-- Update Modal -->
        <Border Grid.RowSpan="3" Background="#990f172a"
                IsVisible="{Binding Update.IsModalVisible}"
                ZIndex="2000">
            <Border Background="White"
                    CornerRadius="24"
                    Padding="32"
                    HorizontalAlignment="Center"
                    VerticalAlignment="Center"
                    Width="480"
                    BoxShadow="0 20 40 0 #40000000">
                <StackPanel Spacing="20">
                    <StackPanel Orientation="Horizontal" Spacing="16" HorizontalAlignment="Center">
                        <Border Background="{StaticResource Slate100Brush}" CornerRadius="12" Padding="12">
                            <material:MaterialIcon Kind="ArrowUpBoldCircleOutline" Width="32" Height="32" Foreground="{StaticResource Slate800Brush}"/>
                        </Border>
                        <TextBlock Text="{Binding [UpdateModalTitle], Source={x:Static services:I18nService.Instance}}"
                                   FontSize="24" FontWeight="Black" Foreground="{StaticResource Slate900Brush}"
                                   VerticalAlignment="Center"/>
                    </StackPanel>

                    <TextBlock Text="{Binding Update.AvailableUpdate.Version, StringFormat={}Новая версия {0}}"
                               FontSize="18" FontWeight="Bold" Foreground="{StaticResource Slate900Brush}"
                               HorizontalAlignment="Center"/>

                    <TextBlock Text="{Binding Update.AvailableUpdate.Notes}"
                               FontSize="14" Foreground="{StaticResource Slate600Brush}"
                               TextWrapping="Wrap" HorizontalAlignment="Center"
                               IsVisible="{Binding Update.AvailableUpdate.Notes, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>

                    <StackPanel Spacing="8" IsVisible="{Binding Update.IsDownloading}">
                        <TextBlock Text="{Binding [UpdateDownloading], Source={x:Static services:I18nService.Instance}}"
                                   FontSize="13" Foreground="{StaticResource Slate600Brush}" HorizontalAlignment="Center"/>
                        <ProgressBar Minimum="0" Maximum="1" Value="{Binding Update.DownloadProgress}" Height="8"/>
                    </StackPanel>

                    <TextBlock Text="{Binding [UpdateBlockedByCart], Source={x:Static services:I18nService.Instance}}"
                               FontSize="13" Foreground="{StaticResource Red500Brush}" HorizontalAlignment="Center"
                               IsVisible="{Binding !Update.CanInstall}"/>

                    <TextBlock Text="{Binding Update.ErrorText}"
                               FontSize="13" Foreground="{StaticResource Red500Brush}"
                               TextWrapping="Wrap" HorizontalAlignment="Center"
                               IsVisible="{Binding Update.ErrorText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>

                    <Grid ColumnDefinitions="*, *" Margin="0,8,0,0">
                        <Button Grid.Column="0" Margin="0,0,6,0" HorizontalAlignment="Stretch"
                                Content="{Binding [UpdateLaterButton], Source={x:Static services:I18nService.Instance}}"
                                Command="{Binding Update.CloseModalCommand}"/>
                        <Button Grid.Column="1" Margin="6,0,0,0" HorizontalAlignment="Stretch"
                                Content="{Binding [UpdateInstallButton], Source={x:Static services:I18nService.Instance}}"
                                Command="{Binding Update.StartUpdateCommand}"
                                IsEnabled="{Binding Update.CanInstall}"/>
                    </Grid>
                </StackPanel>
            </Border>
        </Border>
```

- [ ] **Step 4: Check the bindings by hand**

XAML bindings in this project are reflective, not compiled — `AvaloniaUseCompiledBindingsByDefault` is `false` in the csproj. A misspelled path builds cleanly and silently shows nothing. Re-read every `{Binding ...}` path added above against `UpdateViewModel` and confirm each member exists and is public: `IsUpdateAvailable`, `OpenModalCommand`, `AppVersionText`, `IsModalVisible`, `AvailableUpdate`, `AvailableUpdate.Version`, `AvailableUpdate.Notes`, `IsDownloading`, `DownloadProgress`, `CanInstall`, `ErrorText`, `CloseModalCommand`, `StartUpdateCommand`.

- [ ] **Step 5: Build and run the app**

Run:

```bash
dotnet build src/VvCash/VvCash.csproj -c Debug -o build/verify
```

Expected: build succeeds. Then start the app and log in. Expected on screen: the status bar's right-hand corner reads `V 1.0.0` instead of `V 2.4.0 • TERMINAL ID: LXP-09921`, and no update badge is present (nothing is published yet).

- [ ] **Step 6: Commit**

```bash
git add src/VvCash/Views/PosView.axaml
git commit -m "feat(update): show the update badge, dialog and the real build version"
```

---

## Task 11: End-to-end verification against a real release

This is the only step that proves the feature works. Everything before it is unit-tested in isolation.

**Files:** none — this is a manual procedure.

- [ ] **Step 1: Build and install the current version**

```bash
powershell -ExecutionPolicy Bypass -File build/installer/build_installer.ps1
```

Install `build/installer/Output/VvCashInstaller.exe` on a test machine. Launch it and confirm the status bar shows `V 1.0.0`.

- [ ] **Step 2: Cut a newer build**

Change `<Version>1.0.0</Version>` to `<Version>1.0.1</Version>` in `src/VvCash/VvCash.csproj`, then run the installer script again. Copy the resulting `VvCashInstaller.exe` somewhere safe — this is what the manifest will point at.

- [ ] **Step 3: Compute its hash**

```bash
powershell -Command "(Get-FileHash build/installer/Output/VvCashInstaller.exe -Algorithm SHA256).Hash.ToLower()"
```

Record the 64-character value and the file size in bytes:

```bash
powershell -Command "(Get-Item build/installer/Output/VvCashInstaller.exe).Length"
```

- [ ] **Step 4: Publish**

Upload the 1.0.1 installer to `https://proffi.io/downloads/proffi-kassa-setup.exe`, replacing what is there. Upload alongside it `kassa-latest.json`:

```json
{
  "product": "vvcash",
  "version": "1.0.1",
  "url": "https://proffi.io/downloads/proffi-kassa-setup.exe",
  "sha256": "<the hash from step 3>",
  "sizeBytes": <the size from step 3>,
  "releasedAt": "2026-07-31",
  "notes": "Проверка автообновления"
}
```

Then confirm the server actually serves it as JSON, not as the SPA fallback:

```bash
curl -sI https://proffi.io/downloads/kassa-latest.json
```

Expected: `content-type: application/json`. **If it says `text/html`, the file did not upload and the register will correctly ignore it** — the whole validation chain exists for exactly this outcome. Fix the upload before continuing.

- [ ] **Step 5: Watch the register find it**

On the test machine running 1.0.0, leave the app open and logged in. Within an hour (the first check fires about a minute after login), the green `ДОСТУПНО ОБНОВЛЕНИЕ` badge appears in the status bar.

- [ ] **Step 6: Confirm the cart guard**

Scan or add any product to the cart, then click the badge. Expected: the dialog opens, "Обновить" is disabled, and "Завершите текущий чек" is shown. Clear the cart. Expected: the button becomes enabled without reopening the dialog.

- [ ] **Step 7: Install**

Click "Обновить". Expected in order: a progress bar; the app closes on its own; a short pause with no wizard; the app reappears by itself. The status bar now reads `V 1.0.1`.

If the app closes and does **not** come back, the second `[Run]` entry from Task 1 is missing or its `Check: WizardSilent` is wrong.

- [ ] **Step 8: Confirm a bad hash is refused**

Edit `kassa-latest.json` on the server, changing one character of `sha256`, and bump `version` to `1.0.2`. On a register still on 1.0.1, wait for the badge and press "Обновить". Expected: the download runs, then "Не удалось загрузить обновление", the app stays open, and `%TEMP%\VvCash\updates\` holds **no `.exe` at all**.

Check for any `.exe`, not for a specific name: the downloaded file is named `VvCashInstaller-<guid>.exe` so that an attacker cannot pre-plant a race against a predictable path between the hash check and the launch.

- [ ] **Step 9: Restore the manifest and record the result**

Put the correct hash back. Write down which steps passed. If step 8 did not behave as described, stop — the integrity check is the only thing standing between a corrupted download and an unattended install on every register.

---

## Self-Review Notes

Checked against the spec, section by section:

- Version as a build property → Task 1; `IAppVersionProvider` → Task 2.
- Manifest format and all six validation rules → Task 4 (`Parse`), with one test per rule.
- SPA fallback trap → Task 3 enables it, Task 4 tests it, Task 11 step 4 checks it against the live server.
- HTTPS-only and SHA-256 before launch → Task 4 (scheme check) and Task 5 (hash), verified live in Task 11 step 8.
- Token must not reach proffi.io → Task 8 step 1, registration without `AuthHeaderHandler`.
- Hourly check in the existing loop, first run delayed → Task 8 step 4.
- Badge, real version, modal, cart guard → Tasks 9 and 10.
- Shutdown before install and relaunch after → Task 6 (`Launch`), Task 7 (`ShutdownRequested`), Task 1 step 4 (`Check: WizardSilent`).
- Error table → covered by `UpdateViewModelTest` (download failure, launch failure) and `UpdateServiceTest` (network failure, HTTP error, bad hash).

One deviation from the spec, made deliberately: the spec says the temp directory is cleared at the start of each **check**. The plan clears it at the start of each **download** instead. On the spec's wording an hourly check could delete an installer the cashier is in the middle of downloading. The spec has been amended to match.
