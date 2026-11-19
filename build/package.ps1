#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publishes Comet for one platform and builds its installer with Velopack.

.DESCRIPTION
    Output goes to artifacts/installers/<runtime>:
      Windows  CometMudClient-<channel>-Setup.exe and a portable zip
      macOS    a .pkg installer and a portable zip (must be built on a Mac)
      Linux    an AppImage

    The installers are not code-signed. See README.md, "Installers".

.EXAMPLE
    ./build/package.ps1 -Runtime win-x64
#>
[CmdletBinding()]
param(
    # .NET runtime identifier: win-x64, win-arm64, osx-arm64, osx-x64, linux-x64 or linux-arm64.
    [Parameter(Mandatory)]
    [ValidatePattern('^(win|osx|linux)-(x64|arm64)$')]
    [string] $Runtime,

    # Three-part version. Defaults to <Version> in Directory.Build.props.
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string] $Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not $Version) {
        $props = [xml](Get-Content -Raw (Join-Path $root 'Directory.Build.props'))
        $Version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
        if (-not $Version) { throw 'No <Version> found in Directory.Build.props.' }
    }

    $os = $Runtime.Split('-')[0]
    $publishDir = Join-Path $root "artifacts/publish/$Runtime"
    $outputDir = Join-Path $root "artifacts/installers/$Runtime"
    foreach ($dir in $publishDir, $outputDir) {
        if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    }

    dotnet tool restore
    dotnet publish src/Comet.App/Comet.App.csproj --configuration Release --runtime $Runtime --self-contained `
        --output $publishDir -p:Version=$Version -p:DebugType=none

    $pack = @(
        'pack'
        '--packId', 'CometMudClient'
        '--packVersion', $Version
        '--packDir', $publishDir
        '--packTitle', 'Comet MUD Client'
        '--packAuthors', 'Comet MUD Client'
        '--runtime', $Runtime
        # One update channel per platform and CPU, so their release files never collide.
        '--channel', $Runtime
        '--outputDir', $outputDir
    )
    switch ($os) {
        'win' {
            $pack += '--mainExe', 'Comet.exe', '--icon', 'src/Comet.App/Assets/comet.ico'
        }
        'osx' {
            $pack += '--mainExe', 'Comet', '--icon', 'src/Comet.App/Assets/comet.icns', '--bundleId', 'io.github.cometmudclient'
        }
        'linux' {
            $pack += '--mainExe', 'Comet', '--icon', 'src/Comet.App/Assets/comet.png', '--categories', 'Game;RolePlaying'
        }
    }
    dotnet vpk @pack

    Write-Host "`nInstaller files in ${outputDir}:"
    Get-ChildItem $outputDir | ForEach-Object { '  {0,-48} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) } | Write-Host
}
finally {
    Pop-Location
}
