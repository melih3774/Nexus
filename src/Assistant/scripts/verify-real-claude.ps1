<#
  Runs PC Assistant's real Claude Code integration suite.

  Everything happens in a disposable temporary workspace outside this repository.
  The normal `dotnet test` run never touches Claude Code; only this script does.

  Usage:   .\scripts\verify-real-claude.ps1
#>
[CmdletBinding()]
param(
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$config = if ($Release) { 'Release' } else { 'Debug' }

Write-Host "Building ($config)..." -ForegroundColor Cyan
dotnet build "$repo\PcAssistant.slnx" -c $config -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$env:PCASSISTANT_REAL_CLAUDE = '1'
try {
    Write-Host "`nRunning the real Claude Code integration suite..." -ForegroundColor Cyan
    dotnet test "$repo\tests\PcAssistant.Tests\PcAssistant.Tests.csproj" `
        -c $config --nologo --no-build `
        --filter 'Category=RealClaude' `
        --logger 'console;verbosity=detailed'
}
finally {
    Remove-Item Env:\PCASSISTANT_REAL_CLAUDE -ErrorAction SilentlyContinue
}
