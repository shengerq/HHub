[CmdletBinding()]
param(
    [string]$CompilerPath = (Join-Path $PSScriptRoot '../../.tools/inno/ISCC.exe'),
    [string]$PayloadDirectory,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/unified-v1/installer')
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $PayloadDirectory) {
    $latest = Get-Content -LiteralPath (Join-Path $workspace 'artifacts/unified-v1/latest-build.json') -Raw | ConvertFrom-Json
    $PayloadDirectory = $latest.host
}
$payload = [IO.Path]::GetFullPath($PayloadDirectory)
foreach ($required in @('Hhub.Preview.exe','Hhub.Preview.dll','Hhub.Preview.pri','App.xbf','Assets/Hhub.ico','Hhub.UnifiedHost.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Missing payload: $required" }
}
if (Test-Path -LiteralPath (Join-Path $payload 'bundled-plugins')) { throw 'Optional plugins must not be bundled' }
if (-not (Test-Path -LiteralPath $CompilerPath)) { throw 'Pass -CompilerPath pointing to Inno Setup 6.7.3 ISCC.exe' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
& $CompilerPath '/Qp' "/DPayloadDir=$payload" "/DInstallerOutputDir=$output" (Join-Path $workspace 'installer/HhubUnified.iss')
if ($LASTEXITCODE -ne 0) { throw 'Unified installer compilation failed' }
$installer = Join-Path $output 'Hhub-Unified-Preview-Setup-x64.exe'
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($installer + '.sha256') -Value ($hash + '  ' + [IO.Path]::GetFileName($installer)) -Encoding ascii
Write-Output "Installer: $installer"
