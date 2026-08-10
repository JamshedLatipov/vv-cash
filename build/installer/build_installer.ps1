# Build the VvCash client installers end-to-end:
#   1. Publish self-contained builds (no .NET needed on client machines)
#        x64 - single file, what the fleet runs
#        x86 - flat layout plus an app-local UCRT, for the registers on 32-bit Windows 7
#   2. Compile both Inno Setup installers -> build/installer/Output/
#
# Both flavors are the same runtime; only the architecture and the packaging differ.
# Windows 7 is not a supported platform for it, and the two things that actually stopped
# it there - the missing Universal CRT and a GPU path that renders nothing - are handled
# below and in RenderingSelector.cs rather than by pinning an older runtime.
#
# Usage:  powershell -ExecutionPolicy Bypass -File build/installer/build_installer.ps1
#
# This file is deliberately pure ASCII, for the same reason release.ps1 beside it is:
# Windows PowerShell 5.1 reads a BOM-less .ps1 in the system ANSI codepage, where a stray
# non-ASCII byte can decode into a smart quote that the tokenizer accepts as a string
# delimiter, breaking the parse only on machines whose codepage is not UTF-8.
$ErrorActionPreference = 'Stop'

$root       = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$proj       = Join-Path $root 'src\VvCash\VvCash.csproj'
$publishX64 = Join-Path $root 'publish\win-x64'
$publishX86 = Join-Path $root 'publish\win-x86'
$iss        = Join-Path $PSScriptRoot 'VvCashInstaller.iss'

# Resolving the version takes two steps, because each one alone fails silently in a
# different way.
#
# First: assert the csproj actually declares <Version>. This has to be an XML check,
# because MSBuild cannot answer it -- when the element is absent entirely, the SDK
# supplies an implicit default of 1.0.0 and -getProperty:Version reports that, happily
# and indistinguishably from a real declaration. Drop the line in a bad merge and every
# release would ship stamped 1.0.0 forever, so registers already on 1.0.0 or newer would
# never see an update and nothing anywhere would report an error.
$declaredVersions = ([xml](Get-Content $proj)).SelectNodes('//PropertyGroup/Version')
if ($declaredVersions.Count -eq 0) { throw "No <Version> element in $proj -- add one to the PropertyGroup. Without it MSBuild silently reports the SDK default of 1.0.0, and every release would ship stamped with that version." }

# Second: ask MSBuild for the evaluated value rather than reading the XML node. Only
# MSBuild applies Condition evaluation, so a Release-only <Version> guarded by a
# Condition would be invisible to XPath, which just takes the first node in document
# order. Evaluate as Release, since that is what this script publishes below.
$version = (& dotnet msbuild $proj -getProperty:Version -p:Configuration=Release -nologo).Trim()
if ($LASTEXITCODE -ne 0) { throw "dotnet msbuild -getProperty:Version failed ($LASTEXITCODE) for $proj" }
if ([string]::IsNullOrWhiteSpace($version)) { throw "MSBuild returned a blank Version for $proj -- fix the <Version> property in the PropertyGroup; otherwise ISCC would fail later with a confusing 'AppVersion directive' error instead of naming the real cause." }
Write-Host "==> Product version from csproj: $version" -ForegroundColor Cyan

# ---------------------------------------------------------------- app-local UCRT

# Windows 7 shipped before the Universal CRT existed, and every .NET Core runtime needs
# it. Without this the register dies at startup with a missing api-ms-win-crt-*.dll -- a
# message box and nothing else, since the process never gets far enough to have a window
# or a log. Microsoft's documented answer for a machine that cannot take the Windows
# Update or the redistributable is app-local deployment: ship the DLLs beside the exe.
#
# Sourced from the installed Windows SDK rather than vendored into the repository: these
# are 42 binaries totalling a few megabytes that no diff will ever be read, and the SDK
# is already a prerequisite for anyone building an installer here.
function Copy-AppLocalUcrt($destination) {
    $kitRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Redist'

    # Two layouts exist. Newer SDKs version the Redist directory; the original one did
    # not. Prefer the highest version, fall back to the unversioned path.
    $candidates = @()
    if (Test-Path $kitRoot) {
        $candidates += Get-ChildItem $kitRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName 'ucrt\DLLs\x86' }
        $candidates += (Join-Path $kitRoot 'ucrt\DLLs\x86')
    }

    $source = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $source) {
        throw @"
Could not find the app-local Universal CRT (x86) under $kitRoot.

The 32-bit flavor is unusable without it: the register would install cleanly and then
fail to start on Windows 7 with a missing api-ms-win-crt-*.dll.

Install the Windows 10/11 SDK (any recent version) and re-run. The files wanted are
Redist\<version>\ucrt\DLLs\x86\.
"@
    }

    Write-Host "    UCRT from $source"
    Copy-Item (Join-Path $source 'api-ms-win-*.dll') $destination -Force
    Copy-Item (Join-Path $source 'ucrtbase.dll')     $destination -Force

    # Assert rather than trust the wildcards above. A copy that matched nothing is not an
    # error in PowerShell, and the resulting installer would look perfectly healthy right
    # up until it reached a Windows 7 machine.
    foreach ($required in @('ucrtbase.dll', 'api-ms-win-crt-string-l1-1-0.dll', 'api-ms-win-crt-runtime-l1-1-0.dll')) {
        if (-not (Test-Path (Join-Path $destination $required))) {
            throw "Copied the UCRT from $source but $required is not in $destination. The 32-bit build would fail to start on Windows 7."
        }
    }

    $count = (Get-ChildItem (Join-Path $destination 'api-ms-win-*.dll')).Count
    Write-Host "    $count api-ms-win-*.dll + ucrtbase.dll"
}

# ---------------------------------------------------------------- publish

Write-Host '==> Publishing self-contained x64 build...' -ForegroundColor Cyan
if (Test-Path $publishX64) { Remove-Item $publishX64 -Recurse -Force }
dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publishX64
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (x64) failed ($LASTEXITCODE)" }

Write-Host '==> Publishing self-contained x86 build (Windows 7)...' -ForegroundColor Cyan
if (Test-Path $publishX86) { Remove-Item $publishX86 -Recurse -Force }

# Not single-file, unlike x64. Single-file extracts the bundled native libraries to a
# temp directory and loads them from there, which puts the UCRT search one step further
# from the app directory the DLLs above are copied into. The flat layout is the one
# actually confirmed to start on a 32-bit Windows 7 machine, and this flavor exists for
# exactly one purpose; it is not the place to ship an untested variation of it.
dotnet publish $proj -c Release -r win-x86 --self-contained true -p:PublishSingleFile=false -o $publishX86
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (x86) failed ($LASTEXITCODE)" }

Copy-AppLocalUcrt $publishX86

# ---------------------------------------------------------------- compile

Write-Host '==> Locating Inno Setup compiler (ISCC)...' -ForegroundColor Cyan
$iscc = @(
  'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
  'C:\Program Files\Inno Setup 6\ISCC.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
  $cmd = Get-Command iscc -ErrorAction SilentlyContinue
  if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) { throw 'Inno Setup 6 not found. Install from https://jrsoftware.org/isdl.php' }

Write-Host "==> Compiling installers with $iscc" -ForegroundColor Cyan

& $iscc "/DAppVersion=$version" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed for the x64 flavor ($LASTEXITCODE)" }

& $iscc "/DAppVersion=$version" '/DFlavor=x86' $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed for the x86 flavor ($LASTEXITCODE)" }

$outX64 = Join-Path $PSScriptRoot 'Output\VvCashInstaller.exe'
$outX86 = Join-Path $PSScriptRoot 'Output\VvCashInstaller-x86.exe'
foreach ($built in @($outX64, $outX86)) {
    if (-not (Test-Path $built)) { throw "Expected installer not found at $built" }
}

$mbX64 = [math]::Round((Get-Item $outX64).Length / 1MB, 1)
$mbX86 = [math]::Round((Get-Item $outX86).Length / 1MB, 1)
Write-Host "==> Done." -ForegroundColor Green
Write-Host "    x64  $outX64 ($mbX64 MB)" -ForegroundColor Green
Write-Host "    x86  $outX86 ($mbX86 MB)" -ForegroundColor Green
