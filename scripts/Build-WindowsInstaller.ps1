[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')]
    [string]$Version = '1.0.0',

    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [string]$GOverlayInstallDirectory =
        "${env:ProgramFiles(x86)}\GOverlay",

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repositoryRoot 'artifacts\installer'
$publishRoot = Join-Path $artifactsRoot "publish-$RuntimeIdentifier"
$pluginBuildRoot = Join-Path $artifactsRoot 'plugin-build'
$dependencyRoot = Join-Path $artifactsRoot 'dependencies'
$outputRoot = Join-Path $artifactsRoot 'output'
$solutionPath = Join-Path $repositoryRoot 'DesktopShrine.slnx'
$innoScript = Join-Path $repositoryRoot 'installer\DesktopShrine.iss'
$goverlayMsi = Join-Path $dependencyRoot 'GOverlaySetup.msi'
$goverlayMsiUri =
    'https://www.goverlay.com/downloads/lcdsysinfo/GOverlaySetup.msi'
$goverlayMsiSha256 =
    '7F0B3EBF8422D4D68402B3789944EC8CB9D401E3756751FEEB2AC3AB48F3B5E4'

function Invoke-DotNet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Resolve-InnoCompiler {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidate = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
    if (Test-Path -LiteralPath $candidate) {
        return $candidate
    }

    $candidate = Join-Path $env:LocalAppData 'Programs\Inno Setup 6\ISCC.exe'
    if (Test-Path -LiteralPath $candidate) {
        return $candidate
    }

    throw 'Inno Setup 6 was not found. Run build-installer.cmd to install it with winget, or install it manually.'
}

function Assert-Toolchain {
    $dotnetCommand = Get-Command 'dotnet' -ErrorAction SilentlyContinue
    if (-not $dotnetCommand) {
        throw '.NET 10 SDK was not found.'
    }

    $sdkVersion = (& dotnet --version).Trim()
    if (-not $sdkVersion.StartsWith('10.', [StringComparison]::Ordinal)) {
        throw ".NET 10 SDK is required; dotnet --version returned $sdkVersion."
    }

    $interfacesAssembly = Join-Path $GOverlayInstallDirectory 'Interfaces.dll'
    if (-not (Test-Path -LiteralPath $interfacesAssembly)) {
        throw "GOverlay Interfaces.dll was not found at $interfacesAssembly. Install legacy GOverlay first or pass -GOverlayInstallDirectory. It is required only to compile the GOverlay bridge."
    }
}

function Reset-Directory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolvedRepositoryRoot = [IO.Path]::GetFullPath($repositoryRoot)
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith(
            $resolvedRepositoryRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside the repository: $resolvedPath"
    }

    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolvedPath | Out-Null
}

function Copy-PluginPublish {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$Id
    )

    $projectPath = Join-Path $repositoryRoot $Project
    $temporaryOutput = Join-Path $pluginBuildRoot $Id
    $destination = Join-Path $publishRoot "plugins\$Id"
    New-Item -ItemType Directory -Path $temporaryOutput -Force | Out-Null
    New-Item -ItemType Directory -Path $destination -Force | Out-Null

    Invoke-DotNet publish $projectPath `
        --configuration Release `
        --runtime $RuntimeIdentifier `
        --self-contained false `
        --output $temporaryOutput `
        "-p:Version=$Version" `
        '-p:DebugSymbols=false' `
        '-p:DebugType=None'

    Copy-Item -Path (Join-Path $temporaryOutput '*') `
        -Destination $destination `
        -Recurse `
        -Force
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $projectPath) 'plugin.json') `
        -Destination $destination `
        -Force
}

function Get-VerifiedGOverlayInstaller {
    New-Item -ItemType Directory -Path $dependencyRoot -Force | Out-Null
    if (Test-Path -LiteralPath $goverlayMsi) {
        $existingHash = (Get-FileHash -LiteralPath $goverlayMsi -Algorithm SHA256).Hash
        if ($existingHash -eq $goverlayMsiSha256) {
            return
        }
        Remove-Item -LiteralPath $goverlayMsi -Force
    }

    Write-Host 'Downloading the official GOverlay installer...'
    Invoke-WebRequest -Uri $goverlayMsiUri -OutFile $goverlayMsi
    $downloadedHash = (Get-FileHash -LiteralPath $goverlayMsi -Algorithm SHA256).Hash
    if ($downloadedHash -ne $goverlayMsiSha256) {
        Remove-Item -LiteralPath $goverlayMsi -Force
        throw "The GOverlay installer checksum was $downloadedHash; expected $goverlayMsiSha256. The upstream file may have changed and must be reviewed before packaging."
    }
}

Assert-Toolchain
$innoCompiler = Resolve-InnoCompiler
Reset-Directory $publishRoot
Reset-Directory $pluginBuildRoot
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

if (-not $SkipTests) {
    Invoke-DotNet test $solutionPath --configuration Release
}

$hostProject = Join-Path $repositoryRoot 'src\DesktopShrine.Host\DesktopShrine.Host.csproj'
Invoke-DotNet publish $hostProject `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $publishRoot `
    "-p:Version=$Version" `
    '-p:PublishSingleFile=false' `
    '-p:PublishTrimmed=false' `
    '-p:StageDevelopmentPlugins=false' `
    '-p:DebugSymbols=false' `
    '-p:DebugType=None'

$consoleConfiguration = Join-Path $publishRoot 'configuration\plugins\console-display.json'
if (Test-Path -LiteralPath $consoleConfiguration) {
    Remove-Item -LiteralPath $consoleConfiguration -Force
}

$contractProjects = @(
    'contracts\DesktopShrine.Contracts.Audio\DesktopShrine.Contracts.Audio.csproj',
    'contracts\DesktopShrine.Contracts.Hardware\DesktopShrine.Contracts.Hardware.csproj',
    'contracts\DesktopShrine.Contracts.Media\DesktopShrine.Contracts.Media.csproj'
)
$contractDestination = Join-Path $publishRoot 'contracts'
New-Item -ItemType Directory -Path $contractDestination -Force | Out-Null
foreach ($contractProject in $contractProjects) {
    $contractPath = Join-Path $repositoryRoot $contractProject
    Invoke-DotNet build $contractPath `
        --configuration Release `
        "-p:Version=$Version" `
        '-p:DebugSymbols=false' `
        '-p:DebugType=None'
    $contractName = [IO.Path]::GetFileNameWithoutExtension($contractPath)
    Copy-Item -LiteralPath (Join-Path (
            Split-Path -Parent $contractPath) "bin\Release\net10.0\$contractName.dll") `
        -Destination $contractDestination `
        -Force
}

$plugins = @(
    @{ Project = 'samples\DesktopShrine.Plugin.HardwareMonitor\DesktopShrine.Plugin.HardwareMonitor.csproj'; Id = 'hardware-monitor' },
    @{ Project = 'samples\DesktopShrine.Plugin.AudioCollector\DesktopShrine.Plugin.AudioCollector.csproj'; Id = 'audio-collector' },
    @{ Project = 'samples\DesktopShrine.Plugin.WindowsNowPlaying\DesktopShrine.Plugin.WindowsNowPlaying.csproj'; Id = 'windows-now-playing' },
    @{ Project = 'samples\DesktopShrine.Plugin.SteamNowPlaying\DesktopShrine.Plugin.SteamNowPlaying.csproj'; Id = 'steam-now-playing' },
    @{ Project = 'samples\DesktopShrine.Plugin.ArtworkPalette\DesktopShrine.Plugin.ArtworkPalette.csproj'; Id = 'artwork-palette' },
    @{ Project = 'samples\DesktopShrine.Plugin.BlinkStickBar\DesktopShrine.Plugin.BlinkStickBar.csproj'; Id = 'blinkstick-bar' },
    @{ Project = 'samples\DesktopShrine.Plugin.GOverlay\DesktopShrine.Plugin.GOverlay.csproj'; Id = 'goverlay' }
)
foreach ($plugin in $plugins) {
    Copy-PluginPublish -Project $plugin.Project -Id $plugin.Id
}

$legacyProject = Join-Path $repositoryRoot 'samples\DesktopShrine.Plugin.GOverlay\LegacySdk\DesktopShrine.Plugin.GOverlay.LegacySdk.csproj'
Invoke-DotNet build $legacyProject `
    --configuration Release `
    "-p:GOverlayInstallDirectory=$GOverlayInstallDirectory" `
    '-p:DeployGOverlayPlugin=false' `
    '-p:DebugSymbols=false' `
    '-p:DebugType=None'

$legacyOutput = Join-Path (Split-Path -Parent $legacyProject) 'bin\Release\net48'
$integrationDestination = Join-Path $publishRoot 'integrations\goverlay'
New-Item -ItemType Directory -Path $integrationDestination -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $legacyOutput 'DesktopShrine.Plugin.GOverlay.LegacySdk.Release.dll') `
    -Destination $integrationDestination `
    -Force
Copy-Item -LiteralPath (Join-Path $legacyOutput 'DesktopShrine.Plugin.GOverlay.Layout.dll') `
    -Destination $integrationDestination `
    -Force
Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $legacyProject) 'Deploy-GOverlayPlugin.ps1') `
    -Destination $integrationDestination `
    -Force

Get-VerifiedGOverlayInstaller

& $innoCompiler `
    "/DMyAppVersion=$Version" `
    "/DPublishRoot=$publishRoot" `
    "/DGOverlayMsi=$goverlayMsi" `
    "/DInstallerOutput=$outputRoot" `
    $innoScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$installerPath = Join-Path $outputRoot "DesktopShrine-Setup-$Version-$RuntimeIdentifier.exe"
if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "The expected installer was not created at $installerPath."
}

Write-Host ''
Write-Host "Installer created: $installerPath"
