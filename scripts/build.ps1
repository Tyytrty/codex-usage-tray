[CmdletBinding()]
param(
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\CodexUsageTray.csproj'
$output = Join-Path $PSScriptRoot '..\dist'
$publishArguments = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', $SelfContained.IsPresent.ToString().ToLowerInvariant(), '-o', $output)
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built: $output\CodexUsageTray.exe"
