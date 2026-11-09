[CmdletBinding()]
param([string]$DotnetPath = (Join-Path $PSScriptRoot '../../.tools/dotnet/dotnet.exe'))
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$latest = Get-Content (Join-Path $workspace 'artifacts/unified-v1/latest-build.json') -Raw | ConvertFrom-Json
if (-not $latest.simulation) { throw 'Installer integration checks require the separate simulation build' }
$installer = Join-Path $workspace 'artifacts/unified-v1/installer/Hhub-Unified-Preview-Setup-x64.exe'
$testRoot = Join-Path $workspace ('artifacts/unified-v1/installer-tests/' + [guid]::NewGuid().ToString('N'))
$installed = Join-Path $testRoot 'program'
$profile = Join-Path $testRoot '个人数据 profile'
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{FA108730-86A0-4F40-98F7-B04F44FBF943}_is1'
if (Test-Path -LiteralPath $registration) { throw 'An existing unified preview installation is registered; integration test will not replace it' }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$outside = Join-Path $testRoot 'outside.txt'; Set-Content -LiteralPath $outside -Value 'not owned'
$script:checks = 0
function Assert-Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:checks++; Write-Output "PASS: $Name"
}
function Run-Installer([string]$LogName) {
    $process = Start-Process -FilePath $installer -WindowStyle Hidden -PassThru -ArgumentList @(
        '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/TASKS=',
        "/DIR=`"$installed`"", "/PROFILEDIR=`"$profile`"", "/LOG=`"$(Join-Path $testRoot $LogName)`"")
    $process.WaitForExit(); return $process.ExitCode
}
function Run-Uninstaller([bool]$DeleteData, [string]$LogName) {
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART', "/LOG=`"$(Join-Path $testRoot $LogName)`"")
    if ($DeleteData) { $arguments += '/DELETEUSERDATA' }
    $process = Start-Process -FilePath (Join-Path $installed 'unins000.exe') -WindowStyle Hidden -PassThru -ArgumentList $arguments
    $process.WaitForExit()
    # The bootstrap process can exit before the copied uninstall worker finishes.
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $log = Join-Path $testRoot $LogName
    do {
        $text = ''
        try { if (Test-Path -LiteralPath $log) { $text = [IO.File]::ReadAllText($log) } }
        catch [IO.IOException] { } # The worker may still hold the log exclusively.
        if ($text -match 'Log closed\.') { break }
        Start-Sleep -Milliseconds 100
    } while ($timer.Elapsed.TotalSeconds -lt 15)
    if ($text -notmatch 'Uninstallation process succeeded\.' -or $text -notmatch 'Removed all\? Yes') { return 1 }
    return $process.ExitCode
}
try {
    Assert-Check ((Run-Installer 'install.log') -eq 0) 'fresh per-user installation succeeds'
    Assert-Check ((Test-Path -LiteralPath (Join-Path $installed 'Hhub.Preview.exe')) -and (Test-Path -LiteralPath (Join-Path $profile 'profile.json'))) 'installation creates executable and owned isolated profile'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $installed 'bundled-plugins'))) 'installer does not bundle optional plugins'
    $env:DOTNET_ROOT = Split-Path -Parent ([IO.Path]::GetFullPath($DotnetPath))
    & $DotnetPath run --project (Join-Path $workspace 'tests/Hhub.UnifiedHost.Tests') -c Release -- --prepare-install-fixture $profile (Join-Path $latest.simulationKit 'hhub.simulation.device.hhub') (Join-Path $latest.simulationKit 'test-certification.cer')
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare installer plugin/preset fixture' }
    $preset = @(Get-ChildItem -LiteralPath (Join-Path $profile 'data') -File -Recurse | Where-Object Name -eq 'default.json')[0].FullName
    $presetHash = (Get-FileHash -LiteralPath $preset -Algorithm SHA256).Hash
    Assert-Check ((Run-Installer 'repair.log') -eq 0) 'same-version repair succeeds with a plugin installed'
    Assert-Check ((Get-FileHash -LiteralPath $preset -Algorithm SHA256).Hash -eq $presetHash) 'repair preserves exact preset bytes'
    $versionRecord = Join-Path $installed 'installation-version.txt'
    $version = [IO.File]::ReadAllText($versionRecord); [IO.File]::WriteAllText($versionRecord, 'future-version')
    Assert-Check ((Run-Installer 'blocked-update.log') -ne 0) 'unsupported cross-version overwrite is blocked before installation'
    [IO.File]::WriteAllText($versionRecord, $version)
    Assert-Check ((Get-FileHash -LiteralPath $preset -Algorithm SHA256).Hash -eq $presetHash) 'blocked overwrite preserves existing presets'
    Assert-Check ((Run-Uninstaller $false 'uninstall-keep.log') -eq 0) 'uninstall with explicit preset retention succeeds'
    Assert-Check ((Test-Path -LiteralPath $preset) -and ((Get-FileHash -LiteralPath $preset -Algorithm SHA256).Hash -eq $presetHash)) 'uninstall preserves original personal preset'
    Assert-Check (-not (Test-Path -LiteralPath $installed)) 'uninstall removes the installed program and shared runtime'
    Assert-Check (@(Get-ChildItem -LiteralPath $profile -File -Recurse | Where-Object { $_.Extension -in @('.exe','.dll') }).Count -eq 0) 'keeping presets retains no plugin executable or runtime'
    Assert-Check ((Run-Installer 'reinstall.log') -eq 0) 'reinstall accepts the retained owned profile'
    Assert-Check ((Get-FileHash -LiteralPath $preset -Algorithm SHA256).Hash -eq $presetHash) 'reinstall preserves retained preset bytes'
    Assert-Check ((Run-Uninstaller $true 'uninstall-all.log') -eq 0) 'full uninstall with data deletion succeeds'
    Assert-Check (-not (Test-Path -LiteralPath $profile)) 'full uninstall removes the managed profile'
    Assert-Check (-not (Test-Path -LiteralPath $registration)) 'full uninstall removes its system uninstall registration'
    Assert-Check ((Get-Content -LiteralPath $outside -Raw).Trim() -eq 'not owned') 'uninstall leaves unrelated files untouched'
    [ordered]@{ checks=$script:checks; status='passed'; testRoot=$testRoot; installer=$installer; profile=$profile } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'result.json') -Encoding utf8
    Write-Output "$script:checks installer integration checks passed. Logs: $testRoot"
} finally {
    if (Test-Path -LiteralPath $registration) {
        $location = (Get-ItemProperty -LiteralPath $registration).InstallLocation
        if ([IO.Path]::GetFullPath($location).TrimEnd('\') -eq [IO.Path]::GetFullPath($installed).TrimEnd('\') -and (Test-Path -LiteralPath (Join-Path $installed 'unins000.exe'))) {
            $cleanupCode = Run-Uninstaller $true 'failure-cleanup.log'
            if ($cleanupCode -ne 0) { Write-Warning "Test installation cleanup failed; inspect $testRoot" }
        }
    }
}
