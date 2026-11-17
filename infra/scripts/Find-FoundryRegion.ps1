<#
.SYNOPSIS
    Finds an Azure region that can host every Media Studio Foundry deployment.

.DESCRIPTION
    For each candidate region, runs `az cognitiveservices model list -l <region>` and checks every
    enabled entry of infra/models.json (model name + version + SKU). Those checks are REQUIRED:
    provisioning fails if one is missing. It also scores ADVISORY capabilities from the static
    tables in infra/scripts/regions.json (MAI voices, MAI-Transcribe, Decisions, Content
    Understanding, Document Intelligence); each has a local or alternate fallback in the app.

    Prints a matrix and recommends the first region where all required checks pass, preferring
    the highest advisory score. Works with or without azd: flags come from
    infra/main.parameters.json defaults, overridden by environment variables of the same name
    (for example DEPLOY_OPENAI_AUDIO=true), which is how azd passes `azd env set` values to hooks.

    Requires the Azure CLI signed in (`az login`). Read-only: creates nothing.

.PARAMETER Location
    Check this region. Exit code 0 when all required checks pass, 1 otherwise. When it fails,
    the candidate regions are also scanned so an alternative can be recommended.

.PARAMETER Candidates
    Regions to scan (defaults to the candidates in regions.json).

.PARAMETER Json
    Emit a JSON report instead of the matrix.

.EXAMPLE
    ./infra/scripts/Find-FoundryRegion.ps1
.EXAMPLE
    ./infra/scripts/Find-FoundryRegion.ps1 -Location swedencentral
.EXAMPLE
    ./infra/scripts/Find-FoundryRegion.ps1 -Candidates eastus2,westus3 -Json
#>
[CmdletBinding()]
param(
    [string] $Location,
    [string[]] $Candidates,
    [switch] $Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$infraDir = Split-Path -Parent $PSScriptRoot
$catalog = (Get-Content (Join-Path $infraDir 'models.json') -Raw | ConvertFrom-Json).deployments
$parameters = (Get-Content (Join-Path $infraDir 'main.parameters.json') -Raw | ConvertFrom-Json).parameters
$regionData = Get-Content (Join-Path $PSScriptRoot 'regions.json') -Raw | ConvertFrom-Json

function Get-ParameterValue([string] $name) {
    $p = $parameters.PSObject.Properties[$name]
    if (-not $p) { return $null }
    $raw = [string]$p.Value.value
    if ($raw -match '^\$\{([A-Za-z0-9_]+)(?:=([^}]*))?\}$') {
        $fromEnv = [Environment]::GetEnvironmentVariable($Matches[1])
        if (-not [string]::IsNullOrEmpty($fromEnv)) { return $fromEnv }
        return $Matches[2]
    }
    return $raw
}

function Test-Flag([string] $name) {
    if ($name -eq 'always') { return $true }
    return ([string](Get-ParameterValue $name)) -eq 'true'
}

$enabled = @(
    foreach ($d in $catalog) {
        if (-not (Test-Flag $d.enabledBy)) { continue }
        $model = $d.model
        if ($d.role -eq 'embedding') {
            $override = Get-ParameterValue 'cuEmbeddingModel'
            if ($override) { $model = $override }
        }
        [pscustomobject]@{ Role = $d.role; Model = $model; Version = [string]$d.version; Sku = $d.sku }
    }
)
$cuEnabled = Test-Flag 'enableContentUnderstanding'

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Error 'Azure CLI (az) not found. Install it from https://aka.ms/azure-cli and run `az login`.'
    exit 2
}

$caps = $regionData.capabilities
$advisoryNames = @('maiVoices', 'maiTranscribe', 'decisions', 'documentIntelligence')
if ($cuEnabled) { $advisoryNames += 'contentUnderstanding' }

function Test-Region([string] $region) {
    $region = $region.ToLowerInvariant()
    $raw = & az cognitiveservices model list -l $region -o json --only-show-errors 2>$null
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject]@{ Region = $region; Error = "az cognitiveservices model list failed (signed in? valid region?)"; RequiredOk = $false; Models = [ordered]@{}; Missing = @('model list unavailable'); Advisory = [ordered]@{}; Score = 0 }
    }
    $available = @(($raw | Out-String | ConvertFrom-Json) | Where-Object { $_.kind -eq 'AIServices' })

    $models = [ordered]@{}
    $missing = @()
    foreach ($e in $enabled) {
        $hit = $available | Where-Object {
            $_.model.name -eq $e.Model -and [string]$_.model.version -eq $e.Version -and (@($_.model.skus | ForEach-Object { $_.name }) -contains $e.Sku)
        } | Select-Object -First 1
        $models[$e.Role] = [bool]$hit
        if (-not $hit) { $missing += "$($e.Role) ($($e.Model) $($e.Version) $($e.Sku))" }
    }

    $advisory = [ordered]@{}
    foreach ($name in $advisoryNames) {
        $cap = $caps.$name
        if ($name -eq 'decisions') {
            $hasLuna = [bool]($available | Where-Object { $_.model.name -eq $cap.requiresModel } | Select-Object -First 1)
            $advisory[$name] = $hasLuna -and -not (@($cap.excludedRegions) -contains $region)
        } else {
            $advisory[$name] = @($cap.regions) -contains $region
        }
    }
    $score = @($advisory.Values | Where-Object { $_ }).Count

    [pscustomobject]@{
        Region = $region; Error = $null
        RequiredOk = ($missing.Count -eq 0)
        Models = $models; Missing = $missing; Advisory = $advisory; Score = $score
    }
}

$toScan = [System.Collections.Generic.List[string]]::new()
if ($Location) { $toScan.Add($Location.ToLowerInvariant()) }
$candidateList = if ($Candidates) { $Candidates } else { @($regionData.candidates) }

$results = [System.Collections.Generic.List[object]]::new()
foreach ($r in $toScan) {
    if (-not $Json) { Write-Host "Checking $r ..." }
    $results.Add((Test-Region $r))
}
$locationOk = $Location -and $results[0].RequiredOk
if (-not $locationOk) {
    foreach ($r in $candidateList) {
        if ($toScan -contains $r.ToLowerInvariant()) { continue }
        if (-not $Json) { Write-Host "Checking $r ..." }
        $results.Add((Test-Region $r))
    }
}

$recommended = $results |
    Where-Object { $_.RequiredOk } |
    Sort-Object -Property @{ Expression = 'Score'; Descending = $true }, @{ Expression = { $results.IndexOf($_) } } |
    Select-Object -First 1
$recommendedRegion = if ($recommended) { $recommended.Region } else { $null }

if ($Json) {
    [ordered]@{
        location        = if ($Location) { $Location.ToLowerInvariant() } else { $null }
        locationOk      = [bool]$locationOk
        recommended     = $recommendedRegion
        tablesRetrieved = $regionData.retrieved
        deployments     = $enabled
        regions         = @($results | ForEach-Object {
            [ordered]@{ region = $_.Region; requiredOk = $_.RequiredOk; models = $_.Models; missing = $_.Missing; advisory = $_.Advisory; score = $_.Score; error = $_.Error }
        })
    } | ConvertTo-Json -Depth 6
} else {
    $ok = [string][char]0x2705
    $no = [string][char]0x274C
    $rows = foreach ($res in $results) {
        $row = [ordered]@{ Region = $res.Region; Required = if ($res.RequiredOk) { $ok } else { $no } }
        foreach ($e in $enabled) { $row[$e.Role] = if ($res.Models[$e.Role]) { $ok } else { $no } }
        foreach ($name in $advisoryNames) { $row[$caps.$name.label] = if ($res.Advisory[$name]) { $ok } else { $no } }
        [pscustomobject]$row
    }
    Write-Host ''
    Write-Host 'Required = every enabled deployment in infra/models.json (name + version + SKU).'
    Write-Host "Advisory columns come from infra/scripts/regions.json (Microsoft Learn, retrieved $($regionData.retrieved)); each has a fallback."
    $rows | Format-Table -Property @($rows)[0].PSObject.Properties.Name -AutoSize | Out-String -Width 400 | Write-Host
    foreach ($res in $results | Where-Object { -not $_.RequiredOk }) {
        $why = if ($res.Error) { $res.Error } else { 'missing ' + ($res.Missing -join ', ') }
        Write-Host "  $($res.Region): $why"
    }
    if ($recommendedRegion) {
        $gaps = @($advisoryNames | Where-Object { -not $recommended.Advisory[$_] } | ForEach-Object { "$($caps.$_.label) -> $($caps.$_.fallback)" })
        Write-Host ''
        if ($locationOk) { Write-Host "Region $recommendedRegion passes all required checks." } else { Write-Host "Recommended region: $recommendedRegion" }
        foreach ($g in $gaps) { Write-Host "  note: $g" }
        if (-not $locationOk) { Write-Host "  azd env set AZURE_LOCATION $recommendedRegion" }
    } else {
        Write-Host ''
        Write-Host 'No scanned region hosts every enabled deployment. Disable an optional model (azd env set DEPLOY_MISTRAL_OCR false), or pass -Candidates with more regions.'
    }
}

if ($Location) { if ($locationOk) { exit 0 } else { exit 1 } }
if ($recommendedRegion) { exit 0 } else { exit 1 }
