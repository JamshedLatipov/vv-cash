# Cut a VvCash release: stamp the version, build the installer, and write the update
# manifest the registers poll.
#
#   Build:   powershell -ExecutionPolicy Bypass -File build/installer/release.ps1 -Version 1.0.1
#   Verify:  powershell -ExecutionPolicy Bypass -File build/installer/release.ps1 -Verify
#
# The verify pass checks what is actually published on the server, which is the only way
# to catch the failure this whole feature is most exposed to: proffi.io is a single-page
# app and answers any path it does not recognise with index.html under status 200, so a
# manifest that never uploaded looks like a successful fetch to anything that only reads
# the status code.
#
# This file is deliberately pure ASCII. Windows PowerShell 5.1 reads a BOM-less .ps1 in
# the system ANSI codepage, where a stray non-ASCII byte can decode into a smart quote
# that the tokenizer accepts as a string delimiter, breaking the parse in a way that only
# shows up on machines whose codepage is not UTF-8.

[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    # Product version to stamp, e.g. 1.0.1. Written to VvCash.csproj, from where MSBuild,
    # the installer and IAppVersionProvider all read it.
    [Parameter(Mandatory = $true, ParameterSetName = 'Build')]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    # Release notes shown to the cashier in the update dialog. Free text, any language.
    [Parameter(ParameterSetName = 'Build')]
    [string]$Notes = '',

    # Check the live manifest and installer on the server instead of building.
    [Parameter(Mandatory = $true, ParameterSetName = 'Verify')]
    [switch]$Verify,

    # Where the registers fetch the manifest from. Must match ManifestUrl in UpdateService.cs.
    [string]$ManifestUrl = 'https://proffi.io/downloads/kassa-latest.json'
)

$ErrorActionPreference = 'Stop'

$root      = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$proj      = Join-Path $root 'src\VvCash\VvCash.csproj'
$publishExe = Join-Path $root 'publish\win-x64\VvCash.exe'
$outputDir = Join-Path $PSScriptRoot 'Output'
$builtExe  = Join-Path $outputDir 'VvCashInstaller.exe'
$manifest  = Join-Path $outputDir 'kassa-latest.json'

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text)   { Write-Host "    OK  $text" -ForegroundColor Green }
function Write-Warn($text) { Write-Host "    !!  $text" -ForegroundColor Yellow }

# Minimal JSON string escaping. Deliberately not ConvertTo-Json: PowerShell 5.1 escapes
# every non-ASCII character as \uXXXX, which is valid but turns a Russian release note
# into an unreadable wall that nobody can eyeball on the server.
function ConvertTo-JsonString($value) {
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $value.ToCharArray()) {
        switch ($ch) {
            '"'  { [void]$sb.Append('\"');  continue }
            '\'  { [void]$sb.Append('\\');  continue }
            "`b" { [void]$sb.Append('\b');  continue }
            "`f" { [void]$sb.Append('\f');  continue }
            "`n" { [void]$sb.Append('\n');  continue }
            "`r" { [void]$sb.Append('\r');  continue }
            "`t" { [void]$sb.Append('\t');  continue }
            default {
                if ([int]$ch -lt 0x20) { [void]$sb.AppendFormat('\u{0:x4}', [int]$ch) }
                else                   { [void]$sb.Append($ch) }
            }
        }
    }
    return $sb.ToString()
}

function Get-Sha256($path) {
    return (Get-FileHash $path -Algorithm SHA256).Hash.ToLower()
}

# ---------------------------------------------------------------- verify mode

if ($PSCmdlet.ParameterSetName -eq 'Verify') {
    Write-Step "Fetching $ManifestUrl"

    try {
        $response = Invoke-WebRequest -Uri $ManifestUrl -UseBasicParsing -TimeoutSec 20
    } catch {
        throw "Could not fetch the manifest: $($_.Exception.Message)"
    }

    $contentType = $response.Headers['Content-Type']
    Write-Host "    Content-Type: $contentType"

    if ($contentType -notmatch '^application/json') {
        Write-Warn 'The server did not answer with application/json.'
        Write-Warn 'proffi.io serves index.html under status 200 for any path it does not know,'
        Write-Warn 'so this almost certainly means the manifest was never uploaded. Registers'
        Write-Warn 'reject this exactly as they should, and will not see the release.'
        throw 'Manifest content type is not application/json.'
    }
    Write-Ok 'Served as JSON.'

    $live = $response.Content | ConvertFrom-Json

    foreach ($field in @('product', 'version', 'url', 'sha256')) {
        if (-not $live.PSObject.Properties.Name.Contains($field)) {
            throw "Manifest is missing the required field '$field'."
        }
    }
    if ($live.product -ne 'vvcash') { throw "Manifest names product '$($live.product)', expected 'vvcash'." }
    if ($live.url -notmatch '^https://') { throw "Manifest url is not https: $($live.url)" }
    if ($live.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "Manifest sha256 is not 64 hex characters: $($live.sha256)" }

    $manifestHost = ([Uri]$ManifestUrl).Host
    $downloadHost = ([Uri]$live.url).Host
    if ($downloadHost -ne $manifestHost) {
        throw "Manifest points at host '$downloadHost' but the manifest itself is served from '$manifestHost'. Registers pin the two together and will reject this."
    }
    Write-Ok "Manifest valid: version $($live.version), host $downloadHost."

    Write-Step "Downloading the published installer to check its hash"
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) "vvcash-release-verify.exe"
    try {
        Invoke-WebRequest -Uri $live.url -UseBasicParsing -OutFile $temp -TimeoutSec 600
        $actual = Get-Sha256 $temp
        $size = (Get-Item $temp).Length

        if ($actual -ne $live.sha256.ToLower()) {
            Write-Warn "Manifest says $($live.sha256.ToLower())"
            Write-Warn "File is     $actual"
            throw 'The published installer does not match the hash in the manifest. Registers will download it, refuse it, and delete it - no register can update until this is fixed.'
        }
        Write-Ok "Installer hash matches ($size bytes)."

        if ($live.PSObject.Properties.Name.Contains('sizeBytes') -and $live.sizeBytes -ne $size) {
            Write-Warn "sizeBytes says $($live.sizeBytes) but the file is $size bytes. Not fatal - it only drives the progress bar - but it means the manifest was not regenerated for this build."
        }
    } finally {
        if (Test-Path $temp) { Remove-Item $temp -Force }
    }

    Write-Host ''
    Write-Host "Published release $($live.version) is consistent and reachable." -ForegroundColor Green
    return
}

# ---------------------------------------------------------------- build mode

Write-Step "Stamping version $Version into VvCash.csproj"

$csprojText = Get-Content $proj -Raw
$matches = [regex]::Matches($csprojText, '<Version>[^<]*</Version>')
if ($matches.Count -eq 0) {
    throw "No <Version> element in $proj. Add one to the PropertyGroup - without it MSBuild reports the SDK default of 1.0.0 and every release would ship stamped with that."
}
if ($matches.Count -gt 1) {
    throw "Found $($matches.Count) <Version> elements in $proj. Refusing to guess which one the build uses."
}

$previous = [regex]::Match($csprojText, '<Version>([^<]*)</Version>').Groups[1].Value
if ($previous -eq $Version) {
    Write-Warn "csproj already says $Version - nothing to stamp."
} else {
    $updated = $csprojText -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
    [System.IO.File]::WriteAllText($proj, $updated, (New-Object System.Text.UTF8Encoding($true)))
    Write-Ok "$previous -> $Version"
}

Write-Step 'Building the installer'
& (Join-Path $PSScriptRoot 'build_installer.ps1')
if ($LASTEXITCODE -ne 0) { throw "build_installer.ps1 failed ($LASTEXITCODE)" }

# The point of this check is that the version has to survive four hops - csproj, MSBuild,
# the published apphost, and the Inno define - and a mismatch anywhere means registers
# compare the wrong number and either never update or update in a loop.
Write-Step 'Checking the version reached the binary'
$fileVersion = (Get-Item $publishExe).VersionInfo.FileVersion
if ($fileVersion -ne "$Version.0") {
    throw "Published VvCash.exe reports FileVersion $fileVersion, expected $Version.0. The version did not survive the build."
}
Write-Ok "VvCash.exe reports $fileVersion"

if (-not (Test-Path $builtExe)) { throw "Installer not found at $builtExe" }

$keptExe = Join-Path $outputDir "VvCashInstaller-$Version.exe"
Copy-Item $builtExe $keptExe -Force

$sha  = Get-Sha256 $keptExe
$size = (Get-Item $keptExe).Length

Write-Step 'Writing the manifest'

$downloadUrl = ([Uri]$ManifestUrl).GetLeftPart([System.UriPartial]::Authority) + '/downloads/proffi-kassa-setup.exe'
$releasedAt  = (Get-Date).ToString('yyyy-MM-dd')

$json = @"
{
  "product": "vvcash",
  "version": "$Version",
  "url": "$downloadUrl",
  "sha256": "$sha",
  "sizeBytes": $size,
  "releasedAt": "$releasedAt",
  "notes": "$(ConvertTo-JsonString $Notes)"
}
"@

# No BOM: the manifest is consumed by System.Text.Json over HTTP, and a BOM is one more
# thing that has to survive an upload tool intact for no benefit.
[System.IO.File]::WriteAllText($manifest, $json, (New-Object System.Text.UTF8Encoding($false)))

# Re-read what was written rather than trusting the string above - this is the artifact
# that decides whether every register in the field updates.
$check = Get-Content $manifest -Raw | ConvertFrom-Json
if ($check.sha256 -ne $sha)      { throw 'Manifest hash does not match the installer it was written for.' }
if ($check.version -ne $Version) { throw 'Manifest version does not match the build.' }
Write-Ok 'Manifest re-read and consistent.'

$mb = [math]::Round($size / 1MB, 1)

Write-Host ''
Write-Host "Release $Version ready." -ForegroundColor Green
Write-Host ''
Write-Host "  installer  $keptExe ($mb MB)"
Write-Host "  sha256     $sha"
Write-Host "  manifest   $manifest"
Write-Host ''
Write-Host 'Upload both, keeping these names:' -ForegroundColor Cyan
Write-Host "  $keptExe  ->  $downloadUrl"
Write-Host "  $manifest  ->  $ManifestUrl"
Write-Host ''
Write-Host 'Then confirm the server really has them:' -ForegroundColor Cyan
Write-Host '  powershell -ExecutionPolicy Bypass -File build/installer/release.ps1 -Verify'
Write-Host ''
Write-Host 'Uploading the installer without the manifest leaves every register on the old' -ForegroundColor Yellow
Write-Host 'version silently. Uploading the manifest without the installer is worse: registers' -ForegroundColor Yellow
Write-Host 'download whatever is there, fail the hash check, and retry every hour.' -ForegroundColor Yellow
