# Real end-to-end verification of PC Assistant's privileged path.
#
# Installs the helper (one Windows UAC confirmation on this PC), then runs the live suite
# against the real service over the real pipe, including the probe that measures what an
# unelevated process can and cannot do with an elevated window.
#
# Everything it runs is read-only or disposable. It installs no software and changes no
# system setting. Pass -Uninstall to remove the helper again and check for orphans.

[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$SkipInstall,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$appDir = Join-Path $repo "src\PcAssistant.Desktop\bin\$Configuration\net10.0-windows"
$payload = Join-Path $appDir 'Elevation'
$hostExe = Join-Path $payload 'PcAssistant.Elevation.Host.exe'
$report = Join-Path $env:LOCALAPPDATA 'PcAssistant\runtime\helper-setup.json'
$installRoot = Join-Path $env:ProgramFiles 'PcAssistant\Elevation'

function Invoke-Setup([string[]]$Arguments) {
    New-Item -ItemType Directory -Force (Split-Path $report) | Out-Null
    if (Test-Path $report) { Remove-Item $report -Force }

    Write-Host 'Windows will now ask for administrator approval on this PC. Approve it.' -ForegroundColor Yellow
    try {
        $p = Start-Process -FilePath $hostExe -Verb RunAs -PassThru -ArgumentList $Arguments
        $p.WaitForExit(240000) | Out-Null
    } catch {
        Write-Host "  elevation declined or unavailable: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }

    if (Test-Path $report) {
        $r = Get-Content $report -Raw | ConvertFrom-Json
        Write-Host "  $($r.message)" -ForegroundColor $(if ($r.ok) { 'Green' } else { 'Red' })
        return [bool]$r.ok
    }

    Write-Host '  no report written - the UAC prompt was not approved' -ForegroundColor Red
    return $false
}

function Show-OrphanCheck {
    Write-Host ''
    Write-Host '== Orphan check ==' -ForegroundColor Cyan
    $svc = Get-Service PcAssistantElevation -ErrorAction SilentlyContinue
    $pipe = @([System.IO.Directory]::GetFiles('\\.\pipe\')) -like '*PcAssistant.Elevation*'
    Write-Host "  service registered : $(if ($svc) { 'YES' } else { 'no' })"
    Write-Host "  install directory  : $(if (Test-Path $installRoot) { 'YES' } else { 'no' })"
    Write-Host "  named pipe present : $(if ($pipe) { 'YES' } else { 'no' })"
}

if (-not (Test-Path $hostExe)) { throw "Build first: dotnet build -c $Configuration" }

if ($Uninstall) {
    Write-Host '== Uninstall ==' -ForegroundColor Cyan
    Invoke-Setup @('--uninstall', '--report', $report) | Out-Null
    Show-OrphanCheck
    return
}

if (-not $SkipInstall) {
    Write-Host '== Install ==' -ForegroundColor Cyan
    if (-not (Invoke-Setup @('--install', '--source', $payload,
                             '--client', (Join-Path $appDir 'PcAssistant.Desktop.exe'),
                             '--owner-sid', ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value,
                             '--report', $report))) {
        throw 'Install did not complete. Approve the UAC prompt and run this again.'
    }
}

$svc = Get-Service PcAssistantElevation -ErrorAction SilentlyContinue
Write-Host "  service: $($svc.Status), start type $($svc.StartType)"

$me = [System.Security.Principal.WindowsPrincipal]::new([System.Security.Principal.WindowsIdentity]::GetCurrent())
$elevated = $me.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host "  this (unelevated) process is elevated: $elevated  <- must be False" `
    -ForegroundColor $(if ($elevated) { 'Red' } else { 'Green' })

Write-Host ''
Write-Host '== Live suite against the real service ==' -ForegroundColor Cyan
$env:PCASSISTANT_ELEVATION_LIVE = '1'
dotnet test (Join-Path $repo 'tests\PcAssistant.Tests\PcAssistant.Tests.csproj') -c $Configuration --nologo `
    --filter 'FullyQualifiedName~PrivilegedHelperLiveTests|FullyQualifiedName~ElevatedWindowBoundaryTests'

Write-Host ''
Write-Host "Elevated-window boundary report: $(Join-Path $env:LOCALAPPDATA 'PcAssistant\elevated-window-boundaries.txt')"
Show-OrphanCheck
