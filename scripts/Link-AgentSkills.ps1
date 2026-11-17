#requires -Version 5.1
<#
.SYNOPSIS
    Makes the repository's media-studio-* agent skills visible to Claude Code (.claude/skills) and OpenAI Codex
    (.agents/skills) by linking them to .github/skills. Opt-in; the links are ignored by git.
.DESCRIPTION
    .github/skills is the only copy of each skill. GitHub Copilot already reads it, so Copilot users don't need this
    script (Copilot also reads .claude/skills and .agents/skills and would list each skill twice).
    For every .github/skills/media-studio-* folder the script creates <agent folder>/skills/<name> as a directory
    junction on Windows (no admin rights needed) or a symbolic link elsewhere. It is idempotent, never replaces a real
    folder and, with -Remove, deletes only links that point back into .github/skills.
    media-explanation is runtime guidance for the app, not a coding-agent skill, so it is not linked.
.PARAMETER Agent
    claude (.claude/skills), codex (.agents/skills) or both (default).
.PARAMETER Remove
    Removes the links this script created.
.EXAMPLE
    .\scripts\Link-AgentSkills.ps1 -WhatIf
.EXAMPLE
    .\scripts\Link-AgentSkills.ps1 -Agent claude
.EXAMPLE
    .\scripts\Link-AgentSkills.ps1 -Remove
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive setup script; console output is intended.')]
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('claude', 'codex')] [string[]] $Agent = @('claude', 'codex'),
    [switch] $Remove
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repo '.github\skills'
$folders = @{ claude = '.claude'; codex = '.agents' }
$onWindows = [IO.Path]::DirectorySeparatorChar -eq '\'

function Get-LinkTarget([string] $path) {
    $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    if (-not $item -or -not $item.LinkType) { return $null }
    $target = @($item.Target)[0]
    if (-not $target) { return $null }
    if (-not [IO.Path]::IsPathRooted($target)) { $target = Join-Path (Split-Path -Parent $path) $target }
    return [IO.Path]::GetFullPath($target).TrimEnd('\', '/')
}

$skills = @(Get-ChildItem -LiteralPath $source -Directory -Filter 'media-studio-*' |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'SKILL.md') })
if ($skills.Count -eq 0) { throw "No media-studio-* skills found under $source." }

foreach ($name in $Agent) {
    $root = Join-Path (Join-Path $repo $folders[$name]) 'skills'
    Write-Host "$name -> $root"
    foreach ($skill in $skills) {
        $link = Join-Path $root $skill.Name
        $expected = [IO.Path]::GetFullPath($skill.FullName).TrimEnd('\', '/')
        $current = Get-LinkTarget $link
        if ($Remove) {
            if ($current -and $current -eq $expected) {
                if ($PSCmdlet.ShouldProcess($link, 'Remove skill link')) {
                    [IO.Directory]::Delete($link, $false)
                    Write-Host "  removed $($skill.Name)"
                }
            }
            continue
        }
        if ($current -eq $expected) { Write-Host "  ok      $($skill.Name)"; continue }
        if (Test-Path -LiteralPath $link) {
            Write-Warning "$link already exists and is not a link to $expected; leaving it alone."
            continue
        }
        if ($PSCmdlet.ShouldProcess($link, "Link to $expected")) {
            if (-not (Test-Path -LiteralPath $root)) { New-Item -ItemType Directory -Path $root -Force | Out-Null }
            $type = if ($onWindows) { 'Junction' } else { 'SymbolicLink' }
            New-Item -ItemType $type -Path $link -Target $expected | Out-Null
            Write-Host "  linked  $($skill.Name)"
        }
    }
    if ($Remove) {
        foreach ($dir in @($root, (Split-Path -Parent $root))) {
            if ((Test-Path -LiteralPath $dir) -and -not (Get-ChildItem -LiteralPath $dir -Force)) {
                if ($PSCmdlet.ShouldProcess($dir, 'Remove empty folder')) { Remove-Item -LiteralPath $dir }
            }
        }
    }
}
if (-not $Remove -and -not $WhatIfPreference) {
    Write-Host 'Restart the agent (or start a new session) so it picks up the skills.'
}
