#requires -Version 5.1
<#
.SYNOPSIS
    Builds and launches the Media Studio desktop app (Windows). The first launch opens the setup wizard.
.DESCRIPTION
    Uses the repo-local .\.dotnet SDK when present (or -DotnetRoot), otherwise dotnet on PATH, and requires a .NET 11
    SDK (run .\scripts\Install-Prerequisites.ps1 if it is missing). Remaining arguments are passed to the app,
    for example: .\Start-MediaStudio.ps1 --diagnostics   or   .\Start-MediaStudio.ps1 --configure show
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive launcher; colored console output is intended.')]
[CmdletBinding(PositionalBinding = $false)]
param(
    [string] $DotnetRoot,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoBuild,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $StudioArguments = @()
)

$ErrorActionPreference = 'Stop'
if (-not $DotnetRoot -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot '.dotnet\dotnet.exe'))) { $DotnetRoot = Join-Path $PSScriptRoot '.dotnet' }
if ($DotnetRoot) {
    $DotnetRoot = [IO.Path]::GetFullPath($DotnetRoot)
    $dotnet = Join-Path $DotnetRoot 'dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet)) { throw "dotnet.exe was not found in '$DotnetRoot'." }
    $env:DOTNET_ROOT = $DotnetRoot
    $env:PATH = "$DotnetRoot;$env:PATH"
}
else {
    $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { throw 'The .NET SDK was not found. Run .\scripts\Install-Prerequisites.ps1 first.' }
    $dotnet = $command.Source
}
if (-not (& $dotnet --list-sdks | Where-Object { $_ -match '^11\.' })) {
    throw "No .NET 11 SDK found for '$dotnet'. Run .\scripts\Install-Prerequisites.ps1 first."
}

$project = Join-Path $PSScriptRoot 'src\MediaStudio.App\MediaStudio.App.csproj'
$application = Join-Path $PSScriptRoot "src\MediaStudio.App\bin\$Configuration\net11.0-windows10.0.19041.0\MediaStudio.dll"
if (-not $NoBuild -or -not (Test-Path -LiteralPath $application)) {
    & $dotnet build $project -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
}

$studioHome = if ($env:MEDIASTUDIO_HOME) { $env:MEDIASTUDIO_HOME } else { Join-Path $env:LOCALAPPDATA 'MediaStudio' }
if (-not (Test-Path -LiteralPath (Join-Path $studioHome 'settings\model-providers.json')) -and $StudioArguments.Count -eq 0) {
    Write-Host 'First run: the setup wizard will open. For fully local models, run .\scripts\Setup-LocalModels.ps1 first.' -ForegroundColor Cyan
}
& $dotnet $application @StudioArguments
exit $LASTEXITCODE
