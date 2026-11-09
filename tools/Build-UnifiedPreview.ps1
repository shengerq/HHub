[CmdletBinding()]
param(
    [string]$DotnetPath = (Join-Path $PSScriptRoot '../../.tools/dotnet/dotnet.exe'),
    [switch]$WithSimulation
)
$ErrorActionPreference = 'Stop'
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$DotnetPath = [System.IO.Path]::GetFullPath($DotnetPath)
$env:DOTNET_ROOT = Split-Path -Parent $DotnetPath
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$artifactRoot = Join-Path $workspace 'artifacts/unified-v1'
$output = Join-Path $artifactRoot 'host'
$signerArgument = @()
if ($WithSimulation) {
    $sampleOutput = Join-Path $artifactRoot 'sample'
    & $DotnetPath build (Join-Path $workspace 'examples/Hhub.SamplePlugin') -c Release -o $sampleOutput --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Sample build failed' }
    $kit = Join-Path $artifactRoot ('simulation-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,6))
    & $DotnetPath run --project (Join-Path $workspace 'tools/Hhub.PluginTool') -c Release -- test-kit $sampleOutput $kit
    if ($LASTEXITCODE -ne 0) { throw 'Test kit creation failed' }
    $fingerprint = (Get-Content (Join-Path $kit 'test-fingerprint.txt') -Raw).Trim()
    $signerArgument = @("-p:HhubTestSignerFingerprint=$fingerprint")
    $output = Join-Path $artifactRoot 'host-simulation'
}
if (Test-Path -LiteralPath $output) {
    $output = $output + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
}
& $DotnetPath publish (Join-Path $workspace 'src/Hhub.App/Hhub.App.csproj') -c Release -p:Platform=x64 -p:UseUnifiedHost=true @signerArgument -r win-x64 --self-contained true -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'Host publish failed' }
foreach ($resource in @('Hhub.Preview.exe','Hhub.Preview.dll','Hhub.Preview.pri','App.xbf','Assets/Hhub.ico')) {
    if (-not (Test-Path -LiteralPath (Join-Path $output $resource))) { throw "Missing published resource: $resource" }
}
if (Test-Path -LiteralPath (Join-Path $output 'bundled-plugins')) { throw 'Minimal host must not bundle optional plugins' }
$metadata = [ordered]@{ createdAt = (Get-Date).ToString('o'); host = $output; simulation = [bool]$WithSimulation; validation = 'development-preview-not-accepted' }
if ($WithSimulation) { $metadata['simulationKit'] = $kit }
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifactRoot 'latest-build.json') -Encoding utf8
Write-Output "Host: $output/Hhub.Preview.exe"
if ($WithSimulation) { Write-Output "Simulation packages: $kit" }
