# azd postprovision hook: configures Content Understanding, waits for RBAC, probes Decisions and
# MAI speech (warnings only), then writes the app settings with `configure azd`.
# Never prints tokens or keys.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$endpoint = $env:AZURE_FOUNDRY_ENDPOINT
if ([string]::IsNullOrEmpty($endpoint)) { Write-Error 'AZURE_FOUNDRY_ENDPOINT is not set; did provisioning succeed?'; exit 1 }
if (-not $endpoint.EndsWith('/')) { $endpoint += '/' }
$region = $env:AZURE_LOCATION
$resourceId = $env:AZURE_FOUNDRY_RESOURCE_ID
$warnings = [System.Collections.Generic.List[string]]::new()

function Get-CognitiveToken {
    $t = az account get-access-token --resource https://cognitiveservices.azure.com --query accessToken -o tsv --only-show-errors
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($t)) { throw 'Could not get a token with az account get-access-token (run az login).' }
    return $t.Trim()
}

function Invoke-Foundry([string] $method, [string] $url, $body, [string] $token, [hashtable] $extraHeaders = @{}, [string] $contentType = 'application/json') {
    $headers = @{ Authorization = "Bearer $token" } + $extraHeaders
    $p = @{ Method = $method; Uri = $url; Headers = $headers; TimeoutSec = 60 }
    if ($null -ne $body) {
        $p.Body = if ($body -is [string]) { $body } else { $body | ConvertTo-Json -Depth 10 -Compress }
        $p.ContentType = $contentType
    }
    Invoke-WebRequest @p -UseBasicParsing
}

function Get-StatusCode($err) {
    try { return [int]$err.Exception.Response.StatusCode } catch { return 0 }
}

$token = Get-CognitiveToken

# 1. RBAC propagation: poll the OpenAI v1 models list for up to 5 minutes.
Write-Host 'Waiting for data-plane access (RBAC propagation, up to 5 min) ...'
$deadline = (Get-Date).AddMinutes(5)
$rbacOk = $false
while ((Get-Date) -lt $deadline) {
    try {
        Invoke-Foundry GET "${endpoint}openai/v1/models" $null $token | Out-Null
        $rbacOk = $true
        break
    } catch {
        $code = Get-StatusCode $_
        if ($code -ne 401 -and $code -ne 403) { Write-Host "  models endpoint returned $code; retrying" }
        Start-Sleep -Seconds 15
        $token = Get-CognitiveToken
    }
}
if ($rbacOk) { Write-Host '  data-plane access OK' } else { $warnings.Add('RBAC not effective after 5 min; wait a few minutes and run: dotnet run --project src/VideoProduction.Cli -- configure test') }

# 2. Content Understanding defaults.
if ($env:MEDIASTUDIO_CU_ENABLED -eq 'true') {
    $embeddingModel = if ($env:MEDIASTUDIO_CU_EMBEDDING_MODEL) { $env:MEDIASTUDIO_CU_EMBEDDING_MODEL } else { 'text-embedding-3-small' }
    $defaults = @{ modelDeployments = @{ 'gpt-6-luna' = 'chat'; 'gpt-5.4-mini' = 'mini'; $embeddingModel = 'embedding' } }
    Write-Host 'Configuring Content Understanding default model deployments ...'
    try {
        Invoke-Foundry PATCH "${endpoint}contentunderstanding/defaults?api-version=2025-11-01" $defaults $token | Out-Null
        $analyzer = (Invoke-Foundry GET "${endpoint}contentunderstanding/analyzers/prebuilt-documentSearch?api-version=2025-11-01" $null $token).Content | ConvertFrom-Json
        $supported = @()
        if ($analyzer.PSObject.Properties['supportedModels']) {
            $sm = $analyzer.supportedModels
            foreach ($prop in $sm.PSObject.Properties) { $supported += @($prop.Value) }
        }
        if ($supported.Count -gt 0 -and -not ($supported -contains $embeddingModel)) {
            $warnings.Add("prebuilt-documentSearch does not list $embeddingModel in supportedModels. Fix: azd env set CU_EMBEDDING_MODEL text-embedding-3-large; azd provision")
        } else {
            Write-Host '  Content Understanding defaults set'
        }
    } catch {
        $warnings.Add("Content Understanding defaults could not be set (HTTP $(Get-StatusCode $_)); the app falls back to other document paths.")
    }
}

# 3. Decisions probe (gpt-6-luna deployment "chat").
try {
    $probe = @{
        model     = 'chat'
        input     = 'The invoice total is 42 dollars.'
        questions = @(@{ name = 'mentions_amount'; type = 'predicate'; instructions = 'Does the text mention a monetary amount?' })
    }
    Invoke-Foundry POST "${endpoint}openai/v1/decisions" $probe $token | Out-Null
    Write-Host 'Decisions API: OK'
} catch {
    $warnings.Add("Decisions API not available here (HTTP $(Get-StatusCode $_)); the app uses Julia or the heuristic decider.")
}

# 4. MAI-Voice probe: plain bearer on the custom subdomain, then the regional host with aad#.
$ssml = "<speak version='1.0' xml:lang='en-US'><voice name='en-US-Harper:MAI-Voice-2.1-Flash'>Hello</voice></speak>"
$ttsHeaders = @{ 'X-Microsoft-OutputFormat' = 'audio-16khz-32kbitrate-mono-mp3'; 'User-Agent' = 'media-studio-postprovision' }
$speechOk = $false
try {
    Invoke-Foundry POST "${endpoint}tts/cognitiveservices/v1" $ssml $token $ttsHeaders 'application/ssml+xml' | Out-Null
    $speechOk = $true
    Write-Host 'Speech (MAI-Voice): OK via the Foundry endpoint'
} catch {
    if ($region -and $resourceId) {
        try {
            Invoke-Foundry POST "https://$region.tts.speech.microsoft.com/cognitiveservices/v1" $ssml "aad#$resourceId#$token" $ttsHeaders 'application/ssml+xml' | Out-Null
            $speechOk = $true
            Write-Host 'Speech (MAI-Voice): OK via the regional endpoint'
        } catch { }
    }
}
if (-not $speechOk) { $warnings.Add('Speech (MAI) not reachable - the app will use Kokoro/Whisper locally; see docs/troubleshooting.md#speech') }
$token = $null

# 5. Write the app settings.
$cliProject = Join-Path $repoRoot 'src/VideoProduction.Cli'
$localDotnet = @('.dotnet/dotnet.exe', '.dotnet/dotnet') | ForEach-Object { Join-Path $repoRoot $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
$dotnet = if ($localDotnet) { $localDotnet } elseif (Get-Command dotnet -ErrorAction SilentlyContinue) { 'dotnet' } else { $null }
$configureCmd = 'dotnet run --project src/VideoProduction.Cli -- configure azd'
$configured = $false
if ($dotnet -and (Test-Path $cliProject)) {
    Write-Host "Running: $configureCmd"
    Push-Location $repoRoot
    try {
        & $dotnet run --project $cliProject -- configure azd
        $configured = ($LASTEXITCODE -eq 0)
    } finally { Pop-Location }
    if (-not $configured) { $warnings.Add("configure azd failed; re-run: $configureCmd") }
} else {
    $warnings.Add("dotnet not found; run this to write the app settings: $configureCmd")
}

# 6. Summary.
Write-Host ''
Write-Host '=== Media Studio on Azure ==='
Write-Host "Resource group : $($env:AZURE_RESOURCE_GROUP)"
Write-Host "Region         : $region"
Write-Host "Foundry        : $endpoint"
Write-Host "Project        : $($env:AZURE_FOUNDRY_PROJECT_ENDPOINT)"
Write-Host "Deployments    : $($env:MEDIASTUDIO_DEPLOYMENTS)"
Write-Host 'Cost           : $0 idle - all deployments are pay-per-use GlobalStandard'
foreach ($w in $warnings) { Write-Warning $w }
Write-Host ''
Write-Host 'Next: .\Start-MediaStudio.ps1'
Write-Host 'Teardown (also purges the soft-deleted account and frees quota): azd down --purge'
exit 0
