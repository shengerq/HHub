param([string]$DotnetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    & "$PSScriptRoot/Test-Hygiene.ps1"
    $env:DOTNET_ROOT = Split-Path (Get-Command $DotnetPath).Source -Parent
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    & $DotnetPath build Hhub.Unified.slnx -c Release -p:Platform=x64 -p:UseUnifiedHost=true --nologo
    if ($LASTEXITCODE) { throw 'Unified solution build failed' }
    & $DotnetPath run --project tests/Hhub.UnifiedHost.Tests -c Release
    if ($LASTEXITCODE) { throw 'Unified host checks failed' }
    & $DotnetPath build src/Hhub.App -c Debug -p:Platform=x64 --nologo
    if ($LASTEXITCODE) { throw 'Existing host build failed' }
    & "$PSScriptRoot/Pack-Sdk.ps1" -DotnetPath $DotnetPath
} finally { Pop-Location }
