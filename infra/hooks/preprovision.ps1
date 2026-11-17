# azd preprovision hook: validates the target region before anything is created.
# Override the region check with: azd env set MEDIASTUDIO_SKIP_REGION_CHECK true
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$finder = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/Find-FoundryRegion.ps1'
$hasAzd = [bool](Get-Command azd -ErrorAction SilentlyContinue)

if ([string]::IsNullOrEmpty($env:AZURE_PRINCIPAL_TYPE)) {
    $env:AZURE_PRINCIPAL_TYPE = 'User'
    if ($hasAzd) { azd env set AZURE_PRINCIPAL_TYPE User | Out-Null }
    Write-Host 'AZURE_PRINCIPAL_TYPE not set; using User (set ServicePrincipal for CI).'
}

if ($env:DEPLOY_CLAUDE -eq 'true' -and [string]::IsNullOrWhiteSpace($env:CLAUDE_ORGANIZATION_NAME)) {
    Write-Error 'DEPLOY_CLAUDE=true requires the Marketplace attestation: azd env set CLAUDE_ORGANIZATION_NAME "<your org>" (optional: CLAUDE_COUNTRY_CODE, CLAUDE_INDUSTRY).'
    exit 1
}

if ($env:MEDIASTUDIO_SKIP_REGION_CHECK -eq 'true') {
    Write-Warning 'MEDIASTUDIO_SKIP_REGION_CHECK=true: skipping the region check.'
    exit 0
}

if ([string]::IsNullOrEmpty($env:AZURE_LOCATION)) {
    Write-Host 'AZURE_LOCATION is not set; scanning candidate regions.'
    & $finder
    Write-Error 'Set a region first: azd env set AZURE_LOCATION <region>'
    exit 1
}

Write-Host "Checking that $($env:AZURE_LOCATION) can host every enabled deployment ..."
& $finder -Location $env:AZURE_LOCATION
$code = $LASTEXITCODE
if ($code -eq 1) {
    Write-Error "Region $($env:AZURE_LOCATION) cannot host every enabled deployment (see the matrix above). Pick the recommended region with 'azd env set AZURE_LOCATION <region>', or override with 'azd env set MEDIASTUDIO_SKIP_REGION_CHECK true'."
    exit 1
}
if ($code -ne 0) {
    Write-Error "Region check failed to run (exit $code). Make sure 'az login' is done, or override with 'azd env set MEDIASTUDIO_SKIP_REGION_CHECK true'."
    exit 1
}
exit 0
