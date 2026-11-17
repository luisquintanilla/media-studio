#requires -Version 5.1
<#
.SYNOPSIS
    Pulls the local Ollama models and downloads the pinned speech/decision assets for this machine's tier, then writes
    local-first settings and probes them.
.DESCRIPTION
    1. Detects the hardware tier (same rules as TierSelector: XL only with -Tier XL or -AllowXl and >= 24 GB VRAM).
    2. Pulls the tier's fast, balanced, vision and embedding tags from assets/model-manifest.json (missing ones only).
    3. Runs `video-production assets ensure --tier <T>` (Kokoro, the tier's Whisper model and Julia; hash-verified).
    4. Runs `video-production configure local --tier <T>` and `video-production configure test`.
    Prints the download size first and asks to confirm (-Yes skips). -DryRun only detects and prints the plan.
.EXAMPLE
    .\scripts\Setup-LocalModels.ps1 -DryRun
.EXAMPLE
    .\scripts\Setup-LocalModels.ps1 -Tier M -Family gemma4 -Yes
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive setup script; colored console output is intended.')]
[CmdletBinding()]
param(
    [ValidateSet('S', 'M', 'L', 'XL')] [string] $Tier,
    [ValidateSet('qwen3.5', 'gemma4')] [string] $Family = 'qwen3.5',
    [string] $Fast,
    [string] $Balanced,
    [string] $Vision,
    [switch] $AllowXl,
    [switch] $SkipOllama,
    [switch] $SkipJulia,
    [switch] $Yes,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $repo 'assets\model-manifest.json') -Raw | ConvertFrom-Json
$ollamaUrl = 'http://localhost:11434'

function Get-Hardware {
    $ramBytes = [double](Get-CimInstance -ClassName Win32_ComputerSystem).TotalPhysicalMemory
    $vramBytes = $null; $gpu = $null
    $smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
    if ($smi) {
        foreach ($line in (& $smi.Source --query-gpu=memory.total,name --format=csv,noheader,nounits 2>$null)) {
            $parts = $line -split ',', 2
            $mib = 0.0
            if ([double]::TryParse($parts[0].Trim(), [ref] $mib) -and ($null -eq $vramBytes -or $mib * 1MB -gt $vramBytes)) {
                $vramBytes = $mib * 1MB; $gpu = $parts[1].Trim()
            }
        }
    }
    if ($null -eq $vramBytes) {
        $class = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
        foreach ($key in Get-ChildItem -LiteralPath $class -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^\d+$' }) {
            $adapter = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
            $size = $adapter.'HardwareInformation.qwMemorySize'
            if ($size -is [long] -and ($null -eq $vramBytes -or $size -gt $vramBytes)) { $vramBytes = [double]$size; $gpu = $adapter.DriverDesc }
        }
    }
    [pscustomobject]@{ RamGb = $ramBytes / 1GB; VramGb = $(if ($null -eq $vramBytes) { 0 } else { $vramBytes / 1GB }); Gpu = $gpu }
}

# Mirrors MediaStudio.Configuration.TierSelector (5 % slack for reported totals).
function Select-Tier($hardware, [bool] $allowXl) {
    if ($allowXl -and $hardware.VramGb -ge 24 * 0.95) { return 'XL' }
    if ($hardware.RamGb -ge 32 * 0.95 -and $hardware.VramGb -ge 12 * 0.95) { return 'L' }
    if ($hardware.RamGb -ge 16 * 0.95 -or $hardware.VramGb -ge 8 * 0.95) { return 'M' }
    return 'S'
}

function Get-OllamaModel {
    try { @((Invoke-RestMethod -Uri "$ollamaUrl/api/tags" -TimeoutSec 3).models | ForEach-Object { $_.name }) } catch { $null }
}

function Test-TagPresent([string[]] $installed, [string] $tag) {
    $withLatest = if ($tag.Contains(':')) { $tag } else { "${tag}:latest" }
    return [bool]($installed | Where-Object { $_ -ieq $tag -or $_ -ieq $withLatest })
}

function Get-OllamaTagByte([string] $tag) {
    $name, $version = $tag -split ':', 2
    if (-not $version) { $version = 'latest' }
    if (-not $name.Contains('/')) { $name = "library/$name" }
    try {
        $layers = (Invoke-RestMethod -Uri "https://registry.ollama.ai/v2/$name/manifests/$version" -TimeoutSec 10 `
                -Headers @{ Accept = 'application/vnd.docker.distribution.manifest.v2+json' }).layers
        return [long](($layers | Measure-Object -Property size -Sum).Sum)
    } catch { return $null }
}

function Format-Size($bytes) { if ($null -eq $bytes) { 'unknown size' } else { '{0:N1} GB' -f ($bytes / 1GB) } }

function Get-Dotnet {
    $local = Join-Path $repo '.dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $local) { $env:DOTNET_ROOT = Split-Path $local; return $local }
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $command) { throw 'The .NET 11 SDK was not found. Run .\scripts\Install-Prerequisites.ps1 first.' }
    return $command.Source
}

function Invoke-Cli([string[]] $arguments) {
    Write-Host "> video-production $($arguments -join ' ')" -ForegroundColor Cyan
    if ($DryRun) { return 0 }
    & $script:dotnet $script:cli @arguments | Out-Host
    return $LASTEXITCODE
}

# 1. Tier and models.
$hardware = Get-Hardware
$detected = Select-Tier $hardware ($AllowXl.IsPresent)
if (-not $Tier) { $Tier = $detected }
$gpuText = if ($hardware.VramGb) { '{0:N1} GB VRAM ({1})' -f $hardware.VramGb, $hardware.Gpu } else { 'no discrete GPU detected' }
Write-Host ('Hardware: {0:N1} GB RAM, {1}' -f $hardware.RamGb, $gpuText)
Write-Host "Tier: $Tier$(if ($Tier -ne $detected) { " (detected $detected)" })   Family: $Family"
if ($Tier -eq 'XL' -and $hardware.VramGb -lt 24 * 0.95) { Write-Warning 'Tier XL expects >= 24 GB VRAM; the 27B models may be very slow here.' }

$models = $manifest.ollamaTiers.$Tier
$fastTag = $models.fast; $balancedTag = $models.balanced; $visionTag = $models.vision
if ($Family -ne 'qwen3.5') {
    $alternate = $manifest.ollamaFamilies.$Family.$Tier
    $fastTag = $alternate.fast; $balancedTag = $alternate.balanced; $visionTag = $alternate.vision
}
if ($Fast) { $fastTag = $Fast }
if ($Balanced) { $balancedTag = $Balanced }
if ($Vision) { $visionTag = $Vision }
$tags = @($fastTag, $balancedTag, $visionTag, $models.embedding) | Select-Object -Unique
Write-Host "Models: fast=$fastTag balanced=$balancedTag vision=$visionTag embedding=$($models.embedding)"

# 2. Ollama state.
$pulls = @()
if (-not $SkipOllama) {
    if (-not (Get-Command ollama -ErrorAction SilentlyContinue)) { throw 'Ollama is not installed. Run .\scripts\Install-Prerequisites.ps1 (or pass -SkipOllama).' }
    $installed = Get-OllamaModel
    if ($null -eq $installed -and -not $DryRun) {
        Write-Host 'Starting the Ollama server...'
        Start-Process -FilePath ollama -ArgumentList 'serve' -WindowStyle Hidden
        foreach ($attempt in 1..20) { Start-Sleep -Milliseconds 500; $installed = Get-OllamaModel; if ($null -ne $installed) { break } }
        if ($null -eq $installed) { throw "Ollama did not answer at $ollamaUrl. Start it (ollama serve) and re-run." }
    }
    if ($null -eq $installed) { Write-Host "  (Ollama is not running at $ollamaUrl; every tag is listed as a pull)"; $installed = @() }
    $pulls = @($tags | Where-Object { -not (Test-TagPresent $installed $_) })
    foreach ($tag in $tags) { Write-Host ('  {0} {1}' -f $(if ($pulls -contains $tag) { 'pull    ' } else { 'present ' }), $tag) }
}

# 3. Asset state (`assets ensure` re-uses files that are already present and verified).
$assetIds = @('kokoro-v1.0-q8', $models.whisperAsset)
if (-not $SkipJulia) { $assetIds += 'julia-1' }
$studioHome = if ($env:MEDIASTUDIO_HOME) { $env:MEDIASTUDIO_HOME } else { Join-Path $env:LOCALAPPDATA 'MediaStudio' }
$assetBytes = [long]0
foreach ($id in $assetIds) {
    $asset = $manifest.assets | Where-Object { $_.id -eq $id }
    $missing = @($asset.files | Where-Object {
            $file = Join-Path (Join-Path (Join-Path $studioHome 'models') $asset.dir) $_.path
            -not ((Test-Path -LiteralPath $file) -and (Get-Item -LiteralPath $file).Length -eq $_.size)
        })
    $bytes = [long](($missing | Measure-Object -Property size -Sum).Sum)
    $assetBytes += $bytes
    Write-Host ('  {0} {1} ({2})' -f $(if ($bytes) { 'download' } else { 'present ' }), $id, (Format-Size $bytes))
}

$ollamaBytes = [long]0; $unknown = $false
foreach ($tag in $pulls) { $size = Get-OllamaTagByte $tag; if ($null -eq $size) { $unknown = $true } else { $ollamaBytes += $size } }
Write-Host ('Total download: {0}{1} (Ollama {2}, assets {3})' -f (Format-Size ($ollamaBytes + $assetBytes)),
    $(if ($unknown) { ' + unknown' }), (Format-Size $ollamaBytes), (Format-Size $assetBytes))

if ($DryRun) { Write-Host 'Dry run: nothing is downloaded or written. It would run:' -ForegroundColor Yellow }
elseif (-not $Yes -and ($pulls.Count -or $assetBytes)) {
    if ((Read-Host 'Continue? [y/N]') -notmatch '^(y|yes)$') { Write-Host 'Cancelled.'; exit 1 }
}

# 4. Pull, ensure, configure, test.
foreach ($tag in $pulls) {
    Write-Host "> ollama pull $tag" -ForegroundColor Cyan
    if (-not $DryRun) { & ollama pull $tag; if ($LASTEXITCODE -ne 0) { throw "ollama pull $tag failed." } }
}

$script:cli = Join-Path $repo 'src\VideoProduction.Cli\bin\Release\net11.0\video-production.dll'
Write-Host '> dotnet build src\VideoProduction.Cli -c Release' -ForegroundColor Cyan
if (-not $DryRun) {
    $script:dotnet = Get-Dotnet
    & $script:dotnet build (Join-Path $repo 'src\VideoProduction.Cli\VideoProduction.Cli.csproj') -c Release --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Building the video-production CLI failed.' }
}

if ($SkipJulia) {
    foreach ($id in $assetIds) { if ((Invoke-Cli @('assets', 'ensure', '--id', $id)) -ne 0) { throw "assets ensure --id $id failed." } }
} elseif ((Invoke-Cli @('assets', 'ensure', '--tier', $Tier)) -ne 0) { throw 'assets ensure failed.' }

$configure = @('configure', 'local', '--tier', $Tier)
if ($Tier -eq 'XL') { $configure += '--allow-xl' }
if ($fastTag -ne $models.fast) { $configure += @('--fast', $fastTag) }
if ($balancedTag -ne $models.balanced) { $configure += @('--balanced', $balancedTag) }
if ($visionTag -ne $models.vision) { $configure += @('--vision', $visionTag) }
if ((Invoke-Cli $configure) -ne 0) { throw 'configure local failed.' }

$test = Invoke-Cli @('configure', 'test')
if ($DryRun) { exit 0 }
if ($test -ne 0) { Write-Warning 'Some roles failed `configure test`; see the fixes above.' }
else { Write-Host 'Local models are ready. Start the app with .\Start-MediaStudio.ps1' -ForegroundColor Green }
exit $test
