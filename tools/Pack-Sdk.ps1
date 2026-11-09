param([string]$DotnetPath = 'dotnet', [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/sdk' }
foreach ($project in @('Hhub.PluginContracts','Hhub.PluginSdk','Hhub.UnifiedHost')) {
    & $DotnetPath pack "$root/src/$project/$project.csproj" -c Release -o $OutputDirectory --nologo
    if ($LASTEXITCODE) { throw "Pack failed: $project" }
}
Get-ChildItem $OutputDirectory -Filter '*.nupkg' | Get-FileHash -Algorithm SHA256 | Format-Table
