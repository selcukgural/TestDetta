<#
.SYNOPSIS
    Runs TestDetta in a CI job: Azure DevOps, Jenkins, TeamCity or a pipeline of your own, on Windows agents above all.

.DESCRIPTION
    One script for the three things a pipeline does with TestDetta (docs/ci.md):

      record   on the default branch: run every test once with coverage and save the maps under -MapsDir
      test     on a pull request: run only the tests the change can affect
      shadow   on a pull request, while you evaluate TestDetta: run every test as today and report what the
               selection would have left out (td test --shadow); TestDetta errors never fail the job

    It installs the pinned TestDetta version into a folder of its own (no PATH or global tool changes), fetches
    the full history when the agent cloned shallowly (Azure DevOps does by default), finds the pull request's
    target branch in the agent's environment and fetches it with an explicit refspec. Everything after the
    named parameters goes to dotnet test unchanged, as your own test job passes it.

    Works with Windows PowerShell 5.1 and PowerShell 7 (pwsh, also on Linux and macOS agents).

.EXAMPLE
    # Azure DevOps or Jenkins, default branch
    .\testdetta.ps1 -Mode record -MapsDir \\fileserver\testdetta-maps\shop -- -c Release

.EXAMPLE
    # Pull request: the target branch comes from SYSTEM_PULLREQUEST_TARGETBRANCH (Azure DevOps),
    # CHANGE_TARGET (Jenkins), CI_MERGE_REQUEST_TARGET_BRANCH_NAME (GitLab) or BITBUCKET_PR_DESTINATION_BRANCH
    .\testdetta.ps1 -Mode shadow -MapsDir \\fileserver\testdetta-maps\shop -- -c Release
#>
# No positional binding: every argument without a parameter name goes to dotnet test (-c Release), never to
# -Target or -Version.
[CmdletBinding(PositionalBinding = $false)]
param(
    # record, test or shadow.
    [Parameter(Mandatory = $true)]
    [ValidateSet('record', 'test', 'shadow')]
    [string] $Mode,

    # The folder that keeps the coverage maps between pipelines: a share the agents reach, or a folder your
    # pipeline restores and saves (Azure DevOps Cache@2, an artifact, object storage). Default: $env:TESTDETTA_MAPS.
    [string] $MapsDir = $env:TESTDETTA_MAPS,

    # The pull request's target branch; found in the agent's environment when left out.
    [string] $Target,

    # The TestDetta version to run. Pin it, as the GitHub Action pins its package.
    [string] $Version = $(if ($env:TESTDETTA_VERSION) { $env:TESTDETTA_VERSION } else { '0.5.1' }),

    # Where the tool is installed, one folder per version. Default: the agent's tool cache, else the user profile.
    [string] $ToolsDir,

    # A Markdown summary of the selection (test and shadow); on Azure DevOps it is attached to the build summary.
    [string] $SummaryFile,

    # After recording, keep only this many newest map folders (0 keeps all). Maps are only used for 50 commits.
    [int] $KeepMaps = 0,

    # More td options, before the dotnet test arguments: -TdOptions --project,tests/Shop.Tests/Shop.Tests.csproj
    [string[]] $TdOptions = @(),

    # Arguments for dotnet test, after --.
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $DotnetTestArgs = @()
)

# Exit codes decide, not stderr: when the agent captures the output, Windows PowerShell 5.1 turns every stderr line
# of git, dotnet and td (progress, warnings, logs) into an error record, which 'Stop' would end the script on.
$ErrorActionPreference = 'Continue'
Set-StrictMode -Version 3

function Invoke-Native {
    # Runs a program and stops the script with its exit code when it fails; output goes to the job's log.
    param([string] $File, [string[]] $Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$File $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Get-TargetBranch {
    # The pull request's target branch as the agent names it, without refs/heads/.
    $candidates = @(
        $env:SYSTEM_PULLREQUEST_TARGETBRANCH,        # Azure DevOps
        $env:CHANGE_TARGET,                          # Jenkins multibranch
        $env:CI_MERGE_REQUEST_TARGET_BRANCH_NAME,    # GitLab
        $env:BITBUCKET_PR_DESTINATION_BRANCH,        # Bitbucket
        $env:GITHUB_BASE_REF                         # GitHub Actions
    )
    foreach ($candidate in $candidates) {
        if ($candidate) {
            return ($candidate -replace '^refs/heads/', '')
        }
    }
    return $null
}

if ($DotnetTestArgs.Count -gt 0 -and $DotnetTestArgs[0] -eq '--') {
    $DotnetTestArgs = @($DotnetTestArgs | Select-Object -Skip 1)
}

if (-not $MapsDir) {
    throw 'Give -MapsDir or set TESTDETTA_MAPS: the folder that keeps the coverage maps between pipelines.'
}

# The tool, in a folder of this version: installing is skipped when it is already there.
if (-not $ToolsDir) {
    $cache = if ($env:AGENT_TOOLSDIRECTORY) { $env:AGENT_TOOLSDIRECTORY } else { Join-Path $HOME '.testdetta' }
    $ToolsDir = Join-Path (Join-Path $cache 'testdetta') $Version
}
# $IsWindows does not exist in Windows PowerShell 5.1, which runs only on Windows.
$td = Join-Path $ToolsDir $(if ($env:OS -eq 'Windows_NT') { 'td.exe' } else { 'td' })
if (-not (Test-Path $td)) {
    Write-Host "Installing TestDetta $Version into $ToolsDir"
    Invoke-Native 'dotnet' @('tool', 'install', 'TestDetta', '--version', $Version, '--tool-path', $ToolsDir)
}

# TestDetta compares with the merge base and walks history to find maps; a shallow clone has neither.
if ((& git rev-parse --is-shallow-repository) -eq 'true') {
    Write-Host 'The clone is shallow; fetching the full history.'
    Invoke-Native 'git' @('fetch', '--unshallow', '--no-tags', 'origin')
}

$arguments = New-Object System.Collections.Generic.List[string]
if ($Mode -eq 'record') {
    $arguments.AddRange([string[]] @('record', '--maps-dir', $MapsDir))
}
else {
    if (-not $Target) {
        $Target = Get-TargetBranch
    }
    if (-not $Target) {
        throw 'No pull request target branch: give -Target, or run this on a pull request build.'
    }

    # CI clones often limit the remote's refspecs to the pipeline's own branch; an explicit refspec makes
    # origin/<target> exist.
    Invoke-Native 'git' @('fetch', '--no-tags', 'origin', "+refs/heads/${Target}:refs/remotes/origin/${Target}")
    $arguments.AddRange([string[]] @('test', '--base', "origin/$Target", '--maps-dir', $MapsDir))
    if ($Mode -eq 'shadow') {
        $arguments.Add('--shadow')
    }
    if ($SummaryFile) {
        $arguments.AddRange([string[]] @('--summary-file', $SummaryFile))
    }
}

if ($TdOptions.Count -gt 0) {
    $arguments.AddRange([string[]] $TdOptions)
}

if ($DotnetTestArgs.Count -gt 0) {
    $arguments.Add('--')
    $arguments.AddRange([string[]] $DotnetTestArgs)
}

Write-Host "td $($arguments -join ' ')"
& $td @arguments
$exitCode = $LASTEXITCODE

if ($SummaryFile -and $env:TF_BUILD -and (Test-Path $SummaryFile)) {
    Write-Host "##vso[task.uploadsummary]$((Resolve-Path $SummaryFile).Path)"
}

if ($Mode -eq 'record' -and $exitCode -eq 0 -and $KeepMaps -gt 0 -and (Test-Path $MapsDir)) {
    Get-ChildItem -Path $MapsDir -Directory |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -Skip $KeepMaps |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
}

# 0 success, 1 the analysis failed, 2 invalid command line, 3 tests failed (docs/ci.md).
exit $exitCode
