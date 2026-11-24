[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet',
    [string]$CppCompiler = 'g++.exe',
    [string]$InstallerCompiler = (Join-Path $PSScriptRoot '../../../.tools/inno/ISCC.exe'),
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$hostRepo = Split-Path -Parent $PSScriptRoot
$hostProject = [xml](Get-Content -LiteralPath (Join-Path $hostRepo 'src/Hhub.Desktop/Hhub.Desktop.csproj') -Raw)
$version = [string]$hostProject.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[a-z0-9.]+)?$') { throw 'Invalid desktop version.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $hostRepo "artifacts/release/$version" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$stage = [IO.Directory]::CreateTempSubdirectory('hhub-desktop-release-').FullName

try {
    & (Join-Path $PSScriptRoot 'Build-RebuiltHost.ps1') -Dotnet $Dotnet -CppCompiler $CppCompiler -Package | Out-Host
    $hostZip = Join-Path $hostRepo 'artifacts/HHub-Host-win-x64.zip'
    $payload = Join-Path $stage 'installer-payload'
    [IO.Compression.ZipFile]::ExtractToDirectory($hostZip, $payload)
    [IO.File]::WriteAllText((Join-Path $payload 'hhub-installed.marker'), 'HHub')

    $installerDir = [IO.Directory]::CreateDirectory((Join-Path $stage 'setup')).FullName
    $installerScript = Join-Path $hostRepo 'installer/HhubDesktop.iss'
    & $InstallerCompiler '/Qp' "/DPayloadDir=$payload" "/DInstallerOutputDir=$installerDir" "/DHhubVersion=$version" $installerScript | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed ($LASTEXITCODE)." }
    $installer = Join-Path $installerDir "HHub-$version-Setup-x64.exe"
    if (!(Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Installer was not generated.' }

    [IO.Directory]::CreateDirectory($output) | Out-Null
    foreach ($file in $installer, $hostZip) {
        $destination = Join-Path $output ([IO.Path]::GetFileName($file))
        Copy-Item -LiteralPath $file -Destination $destination -Force
        $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText("$destination.sha256", "$hash  $([IO.Path]::GetFileName($destination))" + [Environment]::NewLine)
        Get-Item -LiteralPath $destination
    }
}
finally {
    $resolved = (Resolve-Path -LiteralPath $stage).ProviderPath
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    if ((Split-Path -Parent $resolved) -ne $temp -or (Split-Path -Leaf $resolved) -notlike 'hhub-desktop-release-*') {
        throw "Unexpected release staging directory: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
