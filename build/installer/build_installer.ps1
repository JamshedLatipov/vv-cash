# Build the VvCash client installer end-to-end:
#   1. Publish a self-contained x64 build (no .NET needed on client machines)
#   2. Compile the Inno Setup installer -> build/installer/Output/VvCashInstaller.exe
#
# Usage:  powershell -ExecutionPolicy Bypass -File build/installer/build_installer.ps1
$ErrorActionPreference = 'Stop'

$root      = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$proj      = Join-Path $root 'src\VvCash\VvCash.csproj'
$publishDir = Join-Path $root 'publish\win-x64'
$iss       = Join-Path $PSScriptRoot 'VvCashInstaller.iss'

# Ask MSBuild for the evaluated Version instead of reading the XML by hand. MSBuild applies
# Condition evaluation the way a real build does; a plain XPath query does not, so a future
# Condition-guarded Release-only <Version> would be silently ignored by XPath and the
# installer would get stamped with the wrong version with no error at all. Evaluate with
# -p:Configuration=Release since that is the configuration this script actually publishes.
$version = (& dotnet msbuild $proj -getProperty:Version -p:Configuration=Release -nologo).Trim()
if ($LASTEXITCODE -ne 0) { throw "dotnet msbuild -getProperty:Version failed ($LASTEXITCODE) for $proj" }
if ([string]::IsNullOrWhiteSpace($version)) { throw "MSBuild returned a blank Version for $proj -- add or fix the <Version> property in the PropertyGroup; otherwise ISCC would fail later with a confusing 'AppVersion directive' error instead of naming the real cause." }
Write-Host "==> Product version from csproj: $version" -ForegroundColor Cyan

Write-Host '==> Publishing self-contained x64 build...' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

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

Write-Host "==> Compiling installer with $iscc" -ForegroundColor Cyan
& $iscc "/DAppVersion=$version" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

$out = Join-Path $PSScriptRoot 'Output\VvCashInstaller.exe'
$mb  = [math]::Round((Get-Item $out).Length / 1MB, 1)
Write-Host "==> Done. Installer: $out ($mb MB)" -ForegroundColor Green
