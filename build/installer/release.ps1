# Cut a VvCash release: stamp the version, build both installers, and write the update
# manifests the registers poll.
#
# Two flavors ship from one version: x64 for the fleet, and x86 for the registers still
# on 32-bit Windows 7. Each has its own installer and its own manifest; a register only
# ever sees the one matching the architecture it runs as.
#
#   Release: powershell -ExecutionPolicy Bypass -File build/installer/release.ps1 -Publish
#   Build:   powershell -ExecutionPolicy Bypass -File build/installer/release.ps1 -Version 1.0.1
#   Verify:  powershell -ExecutionPolicy Bypass -File build/installer/release.ps1 -Verify
#
# With no -Version the next version is derived from the one in the csproj: -Bump patch
# (the default), minor or major. Pass -Version to override that and name one outright.
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
    # the installer and IAppVersionProvider all read it. Optional: left off, the version
    # is derived from what the csproj already says by applying -Bump to it.
    [Parameter(ParameterSetName = 'Build')]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    # Which part to step when -Version is not given. The csproj is the source of truth
    # for the current number, so a release needs no bookkeeping beyond running this.
    [Parameter(ParameterSetName = 'Build')]
    [ValidateSet('patch', 'minor', 'major')]
    [string]$Bump = 'patch',

    # Release notes shown to the cashier in the update dialog. Free text, any language.
    [Parameter(ParameterSetName = 'Build')]
    [string]$Notes = '',

    # Upload to the server over ssh after building, then verify what landed. Connection
    # details come from publish.local.json beside this script - see the template written
    # on first use. That file is gitignored; nothing about the server belongs in the repo.
    [Parameter(ParameterSetName = 'Build')]
    [switch]$Publish,

    # Check the live manifest and installer on the server instead of building.
    [Parameter(Mandatory = $true, ParameterSetName = 'Verify')]
    [switch]$Verify,

    # Where the registers fetch the manifest from. Must match UpdateService.cs, which
    # picks between these two from the architecture of the running process.
    [string]$ManifestUrl = 'https://proffi.io/downloads/kassa-latest.json',

    # The 32-bit flavor's manifest, polled by the registers still on Windows 7. Separate
    # files rather than one manifest describing both, so a 32-bit register cannot be
    # handed the 64-bit installer by a parsing mistake - see UpdateService.cs.
    [string]$ManifestUrlX86 = 'https://proffi.io/downloads/kassa-latest-x86.json'
)

$ErrorActionPreference = 'Stop'

$root      = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$proj      = Join-Path $root 'src\VvCash\VvCash.csproj'
$outputDir = Join-Path $PSScriptRoot 'Output'
$publishCfg = Join-Path $PSScriptRoot 'publish.local.json'

# Everything a release does, it does once per flavor. Kept as a table rather than as two
# parallel sets of variables, because the failure this whole feature is most exposed to
# is doing five of the six steps for one flavor and forgetting the sixth - hashing the
# x64 installer into the x86 manifest, uploading one installer and both manifests, and
# so on. A loop cannot forget a flavor; a copied block can.
#
# The x64 names are exactly what the fleet already polls and downloads. Registers in the
# field must not notice this change at all.
$flavors = @(
    [pscustomobject]@{
        Name         = 'x64'
        PublishExe   = Join-Path $root 'publish\win-x64\VvCash.exe'
        BuiltExe     = Join-Path $outputDir 'VvCashInstaller.exe'
        KeptName     = 'VvCashInstaller-{0}.exe'
        ManifestFile = Join-Path $outputDir 'kassa-latest.json'
        ManifestUrl  = $ManifestUrl
        RemoteExe    = 'proffi-kassa-setup.exe'
    }
    [pscustomobject]@{
        Name         = 'x86'
        PublishExe   = Join-Path $root 'publish\win-x86\VvCash.exe'
        BuiltExe     = Join-Path $outputDir 'VvCashInstaller-x86.exe'
        KeptName     = 'VvCashInstaller-x86-{0}.exe'
        ManifestFile = Join-Path $outputDir 'kassa-latest-x86.json'
        ManifestUrl  = $ManifestUrlX86
        RemoteExe    = 'proffi-kassa-setup-x86.exe'
    }
)

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

# Step one part of a three-part version. Uses [regex]::Match rather than -match on
# purpose: the build path below assigns to $matches itself, which shadows the automatic
# variable -match would write through, and this is not a place to depend on which of the
# two wins. Stepping major or minor zeroes what follows, so 1.4.7 -minor is 1.5.0 and not
# 1.5.7 - a version that would sort above the release it came from while claiming patches
# it never shipped.
function Step-Version([string]$current, [string]$part) {
    $m = [regex]::Match($current, '^(\d+)\.(\d+)\.(\d+)$')
    if (-not $m.Success) {
        throw "Cannot step version '$current' - the csproj must hold a plain three-part version like 1.0.8. Pass -Version to name the next one outright."
    }

    $major = [int]$m.Groups[1].Value
    $minor = [int]$m.Groups[2].Value
    $patch = [int]$m.Groups[3].Value

    switch ($part) {
        'major' { $major++; $minor = 0; $patch = 0 }
        'minor' { $minor++; $patch = 0 }
        default { $patch++ }
    }

    return "$major.$minor.$patch"
}

function Invoke-Native($exe, $argList, $what) {
    & $exe @argList
    if ($LASTEXITCODE -ne 0) { throw "$what failed ($LASTEXITCODE)" }
}

# Upload a file, then move it into place on the server as a second step. scp writes
# straight to the destination path, so a large upload interrupted halfway leaves a
# truncated file live on the server for as long as it takes to notice. Registers would
# download that, fail the hash check and retry hourly. Landing on a temp name and moving
# means the visible file is only ever a complete one.
function Send-File($localPath, $remoteDir, $remoteName, $sshTarget, $sshPort) {
    $tempName = "$remoteName.uploading"
    $scpArgs = @()
    $sshArgs = @()
    if ($sshPort) {
        $scpArgs += @('-P', "$sshPort")
        $sshArgs += @('-p', "$sshPort")
    }
    $scpArgs += @($localPath, "${sshTarget}:$remoteDir/$tempName")
    Invoke-Native 'scp' $scpArgs "scp of $remoteName"

    $sshArgs += @($sshTarget, "mv -f '$remoteDir/$tempName' '$remoteDir/$remoteName'")
    Invoke-Native 'ssh' $sshArgs "remote move of $remoteName"
    Write-Ok "uploaded $remoteName"
}

# ---------------------------------------------------------------- verify

function Invoke-Verify($ManifestUrl) {
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
}

if ($PSCmdlet.ParameterSetName -eq 'Verify') {
    # Both, always. Verifying only the flavor you happened to think about is how the
    # other one sits broken on the server for a week: the registers polling it fail the
    # hash check silently and retry hourly, which looks like nothing at all from here.
    foreach ($flavor in $flavors) {
        Write-Host ''
        Write-Host "--- $($flavor.Name) ---" -ForegroundColor Cyan
        Invoke-Verify $flavor.ManifestUrl
    }
    return
}

# ---------------------------------------------------------------- build mode

$csprojText = Get-Content $proj -Raw
$matches = [regex]::Matches($csprojText, '<Version>[^<]*</Version>')
if ($matches.Count -eq 0) {
    throw "No <Version> element in $proj. Add one to the PropertyGroup - without it MSBuild reports the SDK default of 1.0.0 and every release would ship stamped with that."
}
if ($matches.Count -gt 1) {
    throw "Found $($matches.Count) <Version> elements in $proj. Refusing to guess which one the build uses."
}

$previous = [regex]::Match($csprojText, '<Version>([^<]*)</Version>').Groups[1].Value

if (-not $Version) {
    $Version = Step-Version $previous $Bump
    Write-Step "No -Version given; stepping the $Bump part: $previous -> $Version"
}

# An explicit -Version that goes backwards is worth refusing rather than stamping. The
# registers compare versions numerically (UpdateService.CheckAsync), so shipping a lower
# number than the fleet already runs is a release nobody ever sees, and one that quietly
# strands the real newest build behind a manifest that now names an older one. Equal is
# allowed and only warned about below: re-running the same version is how a release whose
# upload died halfway gets pushed again.
$previousParsed = $null
if ([Version]::TryParse($previous, [ref]$previousParsed) -and [Version]$Version -lt $previousParsed) {
    throw "Refusing to stamp $Version over $previous - it goes backwards. Registers only update to a higher version, so this release would be invisible to the whole fleet."
}

Write-Step "Stamping version $Version into VvCash.csproj"

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

$releasedAt = (Get-Date).ToString('yyyy-MM-dd')

foreach ($flavor in $flavors) {
    Write-Step "Preparing the $($flavor.Name) flavor"

    # The point of this check is that the version has to survive four hops - csproj,
    # MSBuild, the published apphost, and the Inno define - and a mismatch anywhere means
    # registers compare the wrong number and either never update or update in a loop.
    $fileVersion = (Get-Item $flavor.PublishExe).VersionInfo.FileVersion
    if ($fileVersion -ne "$Version.0") {
        throw "Published $($flavor.Name) VvCash.exe reports FileVersion $fileVersion, expected $Version.0. The version did not survive the build."
    }
    Write-Ok "VvCash.exe reports $fileVersion"

    if (-not (Test-Path $flavor.BuiltExe)) { throw "Installer not found at $($flavor.BuiltExe)" }

    $keptExe = Join-Path $outputDir ($flavor.KeptName -f $Version)
    Copy-Item $flavor.BuiltExe $keptExe -Force

    $sha  = Get-Sha256 $keptExe
    $size = (Get-Item $keptExe).Length

    $downloadUrl = ([Uri]$flavor.ManifestUrl).GetLeftPart([System.UriPartial]::Authority) + '/downloads/' + $flavor.RemoteExe

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

    # No BOM: the manifest is consumed by System.Text.Json over HTTP, and a BOM is one
    # more thing that has to survive an upload tool intact for no benefit.
    [System.IO.File]::WriteAllText($flavor.ManifestFile, $json, (New-Object System.Text.UTF8Encoding($false)))

    # Re-read what was written rather than trusting the string above - this is the
    # artifact that decides whether every register in the field updates.
    $check = Get-Content $flavor.ManifestFile -Raw | ConvertFrom-Json
    if ($check.sha256 -ne $sha)      { throw "The $($flavor.Name) manifest hash does not match the installer it was written for." }
    if ($check.version -ne $Version) { throw "The $($flavor.Name) manifest version does not match the build." }
    if ($check.url -ne $downloadUrl) { throw "The $($flavor.Name) manifest points at $($check.url), expected $downloadUrl." }
    Write-Ok 'Manifest re-read and consistent.'

    # Carried on the flavor so the publish and reporting steps below need no second pass
    # over the filesystem to recover what this loop already computed.
    $flavor | Add-Member -NotePropertyName KeptExe     -NotePropertyValue $keptExe    -Force
    $flavor | Add-Member -NotePropertyName Sha         -NotePropertyValue $sha        -Force
    $flavor | Add-Member -NotePropertyName Size        -NotePropertyValue $size       -Force
    $flavor | Add-Member -NotePropertyName DownloadUrl -NotePropertyValue $downloadUrl -Force
}

# Both flavors are cut from one source tree at one version, so two installers claiming
# different versions means the build picked up a stamp mid-flight. Registers would then
# split across two releases with no way to tell from the server which is which.
$distinctHashes = ($flavors | Select-Object -ExpandProperty Sha | Sort-Object -Unique).Count
if ($distinctHashes -ne $flavors.Count) {
    throw 'Two flavors hashed identically. That means the same installer was copied twice - one architecture would be shipped the other one.'
}

Write-Host ''
Write-Host "Release $Version ready." -ForegroundColor Green
foreach ($flavor in $flavors) {
    $mb = [math]::Round($flavor.Size / 1MB, 1)
    Write-Host ''
    Write-Host "  $($flavor.Name)"
    Write-Host "    installer  $($flavor.KeptExe) ($mb MB)"
    Write-Host "    sha256     $($flavor.Sha)"
    Write-Host "    manifest   $($flavor.ManifestFile)"
}

if (-not $Publish) {
    Write-Host ''
    Write-Host 'Upload all four, keeping these names:' -ForegroundColor Cyan
    foreach ($flavor in $flavors) {
        Write-Host "  $($flavor.KeptExe)  ->  $($flavor.DownloadUrl)"
        Write-Host "  $($flavor.ManifestFile)  ->  $($flavor.ManifestUrl)"
    }
    Write-Host ''
    Write-Host 'Then confirm the server really has them:' -ForegroundColor Cyan
    Write-Host '  powershell -ExecutionPolicy Bypass -File build/installer/release.ps1 -Verify'
    Write-Host ''
    Write-Host 'Or let the script do it:  -Publish' -ForegroundColor Cyan
    Write-Host ''
    Write-Host 'Uploading an installer without its manifest leaves those registers on the old' -ForegroundColor Yellow
    Write-Host 'version silently. Uploading a manifest without its installer is worse: registers' -ForegroundColor Yellow
    Write-Host 'download whatever is there, fail the hash check, and retry every hour.' -ForegroundColor Yellow
    return
}

# ---------------------------------------------------------------- publish

if (-not (Test-Path $publishCfg)) {
    $template = @"
{
  "sshTarget": "root@proffi.io",
  "remoteDir": "/var/www/html/downloads",
  "sshPort": null
}
"@
    [System.IO.File]::WriteAllText($publishCfg, $template, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ''
    Write-Warn "No publish config found, so a template was written to:"
    Write-Warn "  $publishCfg"
    Write-Warn 'Fill in the real ssh target and the directory the web server serves /downloads'
    Write-Warn 'from, then re-run with -Publish. The file is gitignored - server details do not'
    Write-Warn 'belong in the repository.'
    throw 'Publish config was missing; a template has been created.'
}

$cfg = Get-Content $publishCfg -Raw | ConvertFrom-Json
foreach ($field in @('sshTarget', 'remoteDir')) {
    if ([string]::IsNullOrWhiteSpace($cfg.$field)) { throw "publish.local.json is missing '$field'." }
}
$sshPort = $null
if ($cfg.PSObject.Properties.Name.Contains('sshPort') -and $cfg.sshPort) { $sshPort = $cfg.sshPort }

Write-Step "Publishing to $($cfg.sshTarget):$($cfg.remoteDir)"

# Order is not a style choice. A manifest is what makes a release visible to the
# registers, so manifests go last: if an installer upload fails, nothing has changed and
# every register stays on the old version. Publish a manifest first and a failed
# installer upload leaves that half of the fleet downloading whatever stale file is still
# there, failing the hash check, and retrying every hour with no way to tell the cashier
# why.
#
# For the same reason the two loops are not merged into one pass per flavor: that would
# put the x64 manifest live before the x86 installer had been sent, and a failure in
# between would leave the server describing a release only half of it can actually serve.
foreach ($flavor in $flavors) {
    $remoteExeName = [System.IO.Path]::GetFileName(([Uri]$flavor.DownloadUrl).AbsolutePath)
    Send-File $flavor.KeptExe $cfg.remoteDir $remoteExeName $cfg.sshTarget $sshPort
}
foreach ($flavor in $flavors) {
    $remoteManifestName = [System.IO.Path]::GetFileName(([Uri]$flavor.ManifestUrl).AbsolutePath)
    Send-File $flavor.ManifestFile $cfg.remoteDir $remoteManifestName $cfg.sshTarget $sshPort
}

foreach ($flavor in $flavors) {
    Write-Host ''
    Write-Host "--- $($flavor.Name) ---" -ForegroundColor Cyan
    Invoke-Verify $flavor.ManifestUrl
}

Write-Host ''
Write-Host "Release $Version is live." -ForegroundColor Green
