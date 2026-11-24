param(
    [string]$Dotnet = 'dotnet',
    [string]$CppCompiler = 'g++.exe',
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\Hhub.Desktop\Hhub.Desktop.csproj'
$output = Join-Path $repo 'src\Hhub.Desktop\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64'
$buildOutput = $output
$traySource = Join-Path $repo 'src\Hhub.Tray'
$compiler = (Get-Command $CppCompiler -CommandType Application).Source
$windres = Join-Path (Split-Path -Parent $compiler) 'windres.exe'
$staging = $null
try {
    if ($Package) {
        $staging = [IO.Directory]::CreateTempSubdirectory('hhub-host-package-').FullName
        $output = Join-Path $staging 'app'
        $buildOutput = Join-Path $staging 'build'
        $resource = Join-Path $staging 'Hhub.Tray.res'
        & $Dotnet publish $project -c Release -p:Platform=x64 -p:DebugType=None -p:DebugSymbols=false "-p:OutDir=$buildOutput/" -o $output --nologo
    }
    else {
        $resource = Join-Path $repo 'src\Hhub.Desktop\obj\Hhub.Tray.res'
        & $Dotnet build $project -c Release -p:Platform=x64 --nologo
    }
    if ($LASTEXITCODE) { throw "WinUI build failed ($LASTEXITCODE)." }

    if ($Package) {
        # WinUI generates the app PRI and XAML binaries in the build directory.
        foreach ($resourceName in 'Hhub.Desktop.pri', 'App.xbf') {
            Copy-Item -LiteralPath (Join-Path $buildOutput $resourceName) -Destination $output -Force
        }
    }

    $tray = Join-Path $output 'Hhub.Tray.exe'
    Push-Location $traySource
    $previousPath = $env:Path
    try {
        $env:Path = (Split-Path -Parent $compiler) + [IO.Path]::PathSeparator + $previousPath
        $version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
        $versionParts = [version]($version -split '-', 2)[0]
        $fileVersion = "$($versionParts.Major),$($versionParts.Minor),$($versionParts.Build),0"
        & $windres "-DHHUB_FILE_VERSION=$fileVersion" "-DHHUB_PRODUCT_VERSION=$version" 'Tray.rc' -O coff -o $resource
        if ($LASTEXITCODE) { throw "Tray resource build failed ($LASTEXITCODE)." }
        $flags = '-std=c++17', '-Os', '-s', '-municode', '-mwindows', '-static', '-Wall', '-Wextra'
        & $compiler 'Tray.cpp' $resource @flags -o $tray -lshell32 -luser32 -lcomctl32 -ladvapi32
        if ($LASTEXITCODE) { throw "Tray build failed ($LASTEXITCODE)." }
    }
    finally {
        $env:Path = $previousPath
        Pop-Location
    }

    if (!$Package) {
        Get-Item -LiteralPath $tray
        return
    }

    foreach ($file in 'Hhub.Desktop.exe', 'Hhub.Desktop.dll', 'Hhub.Desktop.pri', 'Hhub.Desktop.deps.json',
        'Hhub.Desktop.runtimeconfig.json', 'App.xbf', 'Assets/Hhub.ico', 'Hhub.Tray.exe') {
        if (!(Test-Path -LiteralPath (Join-Path $output $file) -PathType Leaf)) {
            throw "Missing host resource: $file"
        }
    }

    $dependencies = Get-Content -LiteralPath (Join-Path $output 'Hhub.Desktop.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($library in $dependencies.libraries.GetEnumerator()) {
        if ($library.Value.type -eq 'project' -and ($library.Key -split '/', 2)[0] -notin
            'Hhub.Desktop', 'Hhub.Core', 'Hhub.UnifiedHost', 'Hhub.PluginContracts', 'Hhub.PluginUi') {
            throw "Unexpected project in the host package: $($library.Key)"
        }
    }

    $extensionPayload = Get-ChildItem -LiteralPath $output -Recurse -Force | Where-Object {
        ($_.PSIsContainer -and $_.Name -in 'plugins', 'bundled-plugins') -or
        (!$_.PSIsContainer -and ($_.Name -in 'plugin.json', 'bundle.json' -or $_.Extension -in '.hhub', '.sys', '.cat' -or
            $_.Name -match '(?i)LibreHardwareMonitor|NAudio|QRCoder|PawnIO|Hhub\.(SystemMonitor|DeviceDiscovery|PhoneRemote|FxSound)'))
    } | Select-Object -First 1
    if ($extensionPayload) { throw "Unexpected extension payload: $($extensionPayload.FullName)" }

    $artifacts = Join-Path $repo 'artifacts'
    [IO.Directory]::CreateDirectory($artifacts) | Out-Null
    $packagePath = Join-Path $artifacts 'HHub-Host-win-x64.zip'
    $archive = Join-Path $staging 'HHub-Host-win-x64.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($output, $archive, [IO.Compression.CompressionLevel]::SmallestSize, $false)
    [IO.File]::Move($archive, $packagePath, $true)
    Get-Item -LiteralPath $packagePath
    $hash = Get-FileHash -LiteralPath $packagePath -Algorithm SHA256
    [IO.File]::WriteAllText($packagePath + '.sha256', "$($hash.Hash.ToLowerInvariant())  HHub-Host-win-x64.zip" + [Environment]::NewLine)
    $hash
}
finally {
    if ($staging) {
        $resolved = (Resolve-Path -LiteralPath $staging).ProviderPath
        $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
        if ((Split-Path -Parent $resolved) -ne $expectedParent -or
            (Split-Path -Leaf $resolved) -notlike 'hhub-host-package-*') {
            throw "Unexpected host staging directory: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
