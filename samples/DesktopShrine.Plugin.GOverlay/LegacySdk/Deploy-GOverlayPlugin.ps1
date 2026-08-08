[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PluginAssemblyPath,

    [Parameter(Mandatory = $true)]
    [string]$InstallDirectory,
    
    [string]$ProcessName = 'GOverlay',

    [switch]$ElevatedCopy,

    [switch]$ElevatedDeployment
)

$ErrorActionPreference = 'Stop'

$SourceDirectory = Split-Path -Parent $PluginAssemblyPath
$PluginAssemblyName = [IO.Path]::GetFileNameWithoutExtension(
    $PluginAssemblyPath
)
$pluginDirectory = Join-Path $InstallDirectory 'Plugins'
$goverlayExecutable = Join-Path $InstallDirectory 'GOverlay.exe'
$filesToCopy = @(
    "$PluginAssemblyName.dll",
    "$PluginAssemblyName.pdb",
    'DesktopShrine.Plugin.GOverlay.Layout.dll',
    'DesktopShrine.Plugin.GOverlay.Layout.pdb',
    'DesktopShrine.Storage.dll',
    'DesktopShrine.Storage.pdb'
)

function Copy-PluginFiles {
    foreach ($fileName in $filesToCopy) {
        $sourcePath = Join-Path $SourceDirectory $fileName
        if (Test-Path -LiteralPath $sourcePath) {
            Copy-Item `
                -LiteralPath $sourcePath `
                -Destination (Join-Path $pluginDirectory $fileName) `
                -Force
        }
    }

    $knownAssemblyNames = @(
        'DesktopShrine.Plugin.GOverlay.LegacySdk',
        'DesktopShrine.Plugin.GOverlay.LegacySdk.Debug',
        'DesktopShrine.Plugin.GOverlay.LegacySdk.Release'
    )
    foreach ($assemblyName in $knownAssemblyNames) {
        if ($assemblyName -eq $PluginAssemblyName) {
            continue
        }

        foreach ($extension in @('.dll', '.pdb')) {
            $stalePath = Join-Path `
                $pluginDirectory `
                ($assemblyName + $extension)
            if (Test-Path -LiteralPath $stalePath) {
                Remove-Item -LiteralPath $stalePath -Force
            }
        }
    }
}

function Stop-GOverlayProcesses {
    $processes = @(
        Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
    )
    if ($processes.Count -eq 0) {
        return
    }

    foreach ($process in $processes) {
        if (-not $process.HasExited -and $process.MainWindowHandle -ne 0) {
            [void]$process.CloseMainWindow()
        }
    }

    foreach ($process in $processes) {
        if (-not $process.HasExited) {
            [void]$process.WaitForExit(5000)
        }
        if (-not $process.HasExited) {
            $process | Stop-Process -Force
            [void]$process.WaitForExit(5000)
        }
    }
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator
    )
}

function Invoke-ElevatedDeployment {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Mode
    )

    Write-Host 'Administrator permission is required to update the GOverlay installation.'
    $quote = {
        param([string]$value)
        "'" + $value.Replace("'", "''") + "'"
    }
    $command = '& {0} -PluginAssemblyPath {1} -InstallDirectory {2} -ProcessName {3} {4}' -f `
        (& $quote $PSCommandPath), `
        (& $quote $PluginAssemblyPath), `
        (& $quote $InstallDirectory), `
        (& $quote $ProcessName), `
        $Mode
    $encodedCommand = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($command)
    )
    $elevated = Start-Process `
        -FilePath 'powershell.exe' `
        -Verb RunAs `
        -ArgumentList @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-EncodedCommand', $encodedCommand
        ) `
        -Wait `
        -PassThru
    if ($elevated.ExitCode -ne 0) {
        throw "Elevated GOverlay deployment failed with exit code $($elevated.ExitCode)."
    }
}

if ($ElevatedDeployment) {
    Stop-GOverlayProcesses
    Copy-PluginFiles
    exit 0
}

if ($ElevatedCopy) {
    Copy-PluginFiles
    exit 0
}

$runningProcesses = @(
    Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
)
$wasRunning = $runningProcesses.Count -gt 0

try {
    $deployedByElevatedProcess = $false
    if (-not (Test-IsAdministrator)) {
        # Request elevation before touching the running display. If UAC is
        # cancelled or cannot be shown, GOverlay remains on the current plugin.
        Invoke-ElevatedDeployment '-ElevatedDeployment'
        $deployedByElevatedProcess = $true
    }
    elseif ($wasRunning) {
        Write-Host "Stopping $ProcessName before deploying the plugin..."
        Stop-GOverlayProcesses
    }

    if (-not $deployedByElevatedProcess) {
        Copy-PluginFiles
    }

    Write-Host "Deployed $PluginAssemblyName to $pluginDirectory."
}
finally {
    $isStillRunning = @(
        Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
    ).Count -gt 0
    if (
        $wasRunning `
        -and -not $isStillRunning `
        -and (Test-Path -LiteralPath $goverlayExecutable)
    ) {
        Write-Host "Restarting $ProcessName..."
        Start-Process `
            -FilePath $goverlayExecutable `
            -WorkingDirectory $InstallDirectory
    }
}
