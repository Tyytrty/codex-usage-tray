[CmdletBinding()]
param(
    [switch]$Startup
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '..\dist\CodexUsageTray.exe'
if (-not (Test-Path $source)) {
    throw 'Build first: .\scripts\build.ps1 -SelfContained'
}

$targetDirectory = Join-Path $env:LOCALAPPDATA 'CodexUsageTray'
New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
$target = Join-Path $targetDirectory 'CodexUsageTray.exe'
Get-Process -Name 'CodexUsageTray' -ErrorAction SilentlyContinue | Stop-Process -Force
Copy-Item $source $target -Force

if ($Startup) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-ItemProperty -Path $runKey -Name 'CodexUsageTray' -Value ('"' + $target + '"') -PropertyType String -Force | Out-Null
}

Start-Process -FilePath $target
Write-Host "Installed and started: $target"
