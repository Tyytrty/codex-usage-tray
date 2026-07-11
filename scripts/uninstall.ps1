[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Get-Process -Name 'CodexUsageTray' -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'CodexUsageTray' -ErrorAction SilentlyContinue
$targetDirectory = Join-Path $env:LOCALAPPDATA 'CodexUsageTray'
if (Test-Path $targetDirectory) { Remove-Item -LiteralPath $targetDirectory -Recurse -Force }
Write-Host 'Codex Usage Tray has been removed.'
