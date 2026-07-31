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

$versionNode = ([xml](Get-Content $proj)).SelectSingleNode('//PropertyGroup/Version')
if (-not $versionNode) { throw "No <Version> element in $proj — the installer needs it." }
$version = $versionNode.InnerText.Trim()
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
