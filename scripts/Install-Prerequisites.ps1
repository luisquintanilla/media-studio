#requires -Version 5.1
<#
.SYNOPSIS
    Installs what Media Studio needs on Windows (idempotent): .NET 11 SDK, WebView2 runtime, FFmpeg, Node.js 24,
    eSpeak NG and Ollama via winget, then the browser worker's npm packages and Playwright Chromium.
.DESCRIPTION
    Every component is detected first and only installed when missing, so re-running is safe. Use -WhatIf to see
    what would be installed without changing anything, and -Skip to leave components out. winget may show a UAC
    prompt for machine-wide installers. Finishes with a status table; exit code 1 when a required item is missing.
.EXAMPLE
    .\scripts\Install-Prerequisites.ps1 -WhatIf
.EXAMPLE
    .\scripts\Install-Prerequisites.ps1 -Skip ollama, browser-worker
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive installer; colored console output is intended.')]
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('dotnet', 'webview2', 'ffmpeg', 'node', 'espeak-ng', 'ollama', 'browser-worker')]
    [string[]] $Skip = @()
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$winget = Get-Command winget -ErrorAction SilentlyContinue
$results = [Collections.Generic.List[object]]::new()

function Get-RefreshedPath {
    $machine = [Environment]::GetEnvironmentVariable('PATH', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('PATH', 'User')
    return (@($machine, $user, $env:PATH) | Where-Object { $_ }) -join ';'
}

function Get-FirstLine([string] $tool, [string[]] $arguments) {
    $command = Get-Command $tool -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { return $null }
    # ollama prefixes its lines with "Warning:" when its server is down ("Warning: client version is 0.x").
    try {
        $lines = @(& $command.Source @arguments 2>&1 | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
        $line = $lines | Where-Object { $_ -notmatch '^Warning:' } | Select-Object -First 1
        if (-not $line) { $line = $lines | Where-Object { $_ -match '\d+\.\d+' } | ForEach-Object { $_ -replace '^Warning:\s*', '' } | Select-Object -First 1 }
        return $line
    }
    catch { return $null }
}

function Get-DotnetSdk {
    $candidates = @((Join-Path $repo '.dotnet\dotnet.exe')) + @(Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | ForEach-Object { $_.Source })
    foreach ($dotnet in $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) }) {
        $sdk = & $dotnet --list-sdks 2>$null | Where-Object { $_ -match '^11\.' } | Select-Object -Last 1
        if ($sdk) { return ($sdk -split ' ')[0] }
    }
    return $null
}

function Get-WebView2 {
    foreach ($key in 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') {
        $version = (Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue).pv
        if ($version -and $version -ne '0.0.0.0') { return $version }
    }
    return $null
}

function Get-Espeak {
    $version = Get-FirstLine 'espeak-ng' @('--version')
    if ($version) { return $version }
    $path = Join-Path $env:ProgramFiles 'eSpeak NG\espeak-ng.exe'
    if (Test-Path -LiteralPath $path) { return 'installed (not on PATH)' }
    return $null
}

function Get-Node {
    $version = Get-FirstLine 'node' @('--version')
    if ($version -match '^v(\d+)\.' -and [int]$Matches[1] -ge 24) {
        if ([int]$Matches[1] -ne 24) { return "$version (workers\browser declares Node 24; newer majors are untested)" }
        return $version
    }
    return $null
}

function Get-Node24WingetVersion {
    $versions = & $winget.Source show --id OpenJS.NodeJS.LTS --versions --source winget --accept-source-agreements 2>$null
    return $versions | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^24\.\d+\.\d+$' } | Select-Object -First 1
}

function Install-Component {
    [CmdletBinding(SupportsShouldProcess)]
    param([string] $Name, [string] $Id, [scriptblock] $Detect, [bool] $Required, [string] $Manual, [string] $Version)
    if ($Skip -contains $name) { $results.Add([pscustomobject]@{ Component = $name; Status = 'skipped'; Detail = '' }); return }
    $found = & $detect
    if (-not $found -and $winget -and $PSCmdlet.ShouldProcess($name, "winget install --id $id")) {
        $arguments = @('install', '--id', $id, '--exact', '--source', 'winget', '--silent', '--accept-package-agreements', '--accept-source-agreements')
        if ($version) { $arguments += @('--version', $version) }
        & $winget.Source @arguments | Out-Host
        $env:PATH = Get-RefreshedPath
        $found = & $detect
    }
    $status = if ($found) { '✅' } elseif ($WhatIfPreference) { 'would install' } elseif ($required) { '❌' } else { '⚠️' }
    $detail = if ($found) { $found } elseif (-not $winget) { "winget not found; install manually: $manual" } else { "winget id $id; $manual" }
    $results.Add([pscustomobject]@{ Component = $name; Status = $status; Detail = $detail; Required = $required })
}

if (-not $winget) { Write-Warning 'winget was not found (install "App Installer" from the Microsoft Store). Missing items are listed with manual links.' }

Install-Component -Name 'dotnet' -Id 'Microsoft.DotNet.SDK.11' -Detect { Get-DotnetSdk } -Required $true -Manual 'https://dotnet.microsoft.com/download/dotnet/11.0'
Install-Component -Name 'webview2' -Id 'Microsoft.EdgeWebView2Runtime' -Detect { Get-WebView2 } -Required $true -Manual 'https://developer.microsoft.com/microsoft-edge/webview2/'
Install-Component -Name 'ffmpeg' -Id 'Gyan.FFmpeg' -Detect { (Get-FirstLine 'ffmpeg' @('-version')) -replace ' Copyright.*$', '' } -Required $false -Manual 'https://www.gyan.dev/ffmpeg/builds/ (video rendering)'
$nodeVersion = if ($winget -and -not (Get-Node) -and $Skip -notcontains 'node') { Get-Node24WingetVersion } else { $null }
Install-Component -Name 'node' -Id 'OpenJS.NodeJS.LTS' -Detect { Get-Node } -Required $true -Manual 'Node.js 24 from https://nodejs.org/' -Version $nodeVersion
Install-Component -Name 'espeak-ng' -Id 'eSpeak-NG.eSpeak-NG' -Detect { Get-Espeak } -Required $false -Manual 'https://github.com/espeak-ng/espeak-ng/releases (local Kokoro narration)'
Install-Component -Name 'ollama' -Id 'Ollama.Ollama' -Detect { Get-FirstLine 'ollama' @('--version') } -Required $false -Manual 'https://ollama.com/download (local models)'

if ($Skip -notcontains 'browser-worker') {
    $worker = Join-Path $repo 'workers\browser'
    $npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
    $ready = (Test-Path (Join-Path $worker 'node_modules\playwright')) -and (Test-Path (Join-Path $env:LOCALAPPDATA 'ms-playwright\chromium-*'))
    if ($ready) { $status = '✅'; $detail = 'node_modules and Playwright Chromium present' }
    elseif (-not $npm) { $status = '❌'; $detail = 'npm not found; install Node.js 24 first' }
    elseif ($PSCmdlet.ShouldProcess('workers\browser', 'npm ci; npx playwright install chromium')) {
        Push-Location $worker
        try {
            & $npm.Source ci --no-audit --no-fund | Out-Host
            if ($LASTEXITCODE -eq 0) { & (Join-Path (Split-Path $npm.Source) 'npx.cmd') playwright install chromium | Out-Host }
            $status = if ($LASTEXITCODE -eq 0) { '✅' } else { '❌' }; $detail = "npm ci + playwright install chromium (exit $LASTEXITCODE)"
        } finally { Pop-Location }
    } else { $status = 'would install'; $detail = 'npm ci; npx playwright install chromium' }
    $results.Add([pscustomobject]@{ Component = 'browser-worker'; Status = $status; Detail = $detail; Required = $true })
}

$results | Format-Table Component, Status, Detail -AutoSize -Wrap | Out-Host
if ($results | Where-Object { $_.Status -eq '❌' }) { Write-Host 'Some required components are missing; see the links above.' -ForegroundColor Red; exit 1 }
if (-not $WhatIfPreference) { Write-Host 'Next: .\scripts\Setup-LocalModels.ps1 (local models) and .\Start-MediaStudio.ps1' -ForegroundColor Green }
