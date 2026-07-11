[CmdletBinding()]
param(
    [switch]$Startup
)

$ErrorActionPreference = 'Stop'
$sourceDirectory = Join-Path $PSScriptRoot '..\dist'
$source = Join-Path $sourceDirectory 'CodexUsageTray.exe'
if (-not (Test-Path -LiteralPath $source)) {
    throw 'Build first: .\scripts\build.ps1 -SelfContained'
}

$targetDirectory = Join-Path $env:LOCALAPPDATA 'CodexUsageTray'
$target = Join-Path $targetDirectory 'CodexUsageTray.exe'
Get-Process -Name 'CodexUsageTray','CodexUsageTrayV2' -ErrorAction SilentlyContinue | Stop-Process -Force
$preserveDirectory = Join-Path $env:TEMP ('CodexUsageTray-preserve-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $targetDirectory) {
    New-Item -ItemType Directory -Force -Path $preserveDirectory | Out-Null
    foreach ($name in @('settings.json', 'diagnostics.log', 'assets')) {
        $item = Join-Path $targetDirectory $name
        if (Test-Path -LiteralPath $item) {
            Copy-Item -LiteralPath $item -Destination $preserveDirectory -Recurse -Force
        }
    }
    Remove-Item -LiteralPath $targetDirectory -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
Copy-Item -Path (Join-Path $sourceDirectory '*') -Destination $targetDirectory -Recurse -Force
if (Test-Path -LiteralPath $preserveDirectory) {
    Copy-Item -Path (Join-Path $preserveDirectory '*') -Destination $targetDirectory -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $preserveDirectory -Recurse -Force
}

if ($Startup) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    Remove-ItemProperty -Path $runKey -Name 'CodexUsageTrayV2' -ErrorAction SilentlyContinue
    New-ItemProperty -Path $runKey -Name 'CodexUsageTray' -Value ('"' + $target + '"') -PropertyType String -Force | Out-Null
}

Start-Process -FilePath $target
Write-Host "Installed and started: $target"
