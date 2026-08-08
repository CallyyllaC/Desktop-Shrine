# Windows installation and dependencies

Desktop Shrine is packaged as a 64-bit Windows background application. It runs
inside the signed-in desktop session so that desktop audio capture, Windows Now
Playing, Steam detection, and USB hardware all remain available. It is not
installed as a Windows service because services run in Session 0 and cannot
reliably use those user-session APIs.

## End-user requirements

- Windows 10 version 1809 or newer, or Windows 11, on x64 hardware.
- No separate .NET 10 runtime. The release is published self-contained and
  carries its own .NET and ASP.NET Core runtime files.
- BlinkStick does not need separate BlinkStick software. Desktop Shrine talks
  to it as a Windows HID device through the bundled HidSharp package.
- Legacy GOverlay LCDSysInfo hardware needs the official GOverlay software and
  the Desktop Shrine bridge plugin. Setup can optionally download and launch
  the official legacy GOverlay 1.6.9 installer, then deploys the bridge after
  GOverlay is present.
- GOverlay uses the Windows .NET Framework 4.x runtime. Supported Windows 10
  and Windows 11 installations already include a compatible 4.x runtime; this
  is unrelated to the self-contained .NET 10 runtime used by Desktop Shrine.
- Steam is optional and is only needed when the Steam Now Playing input is in
  use.

All NuGet libraries used by the host and plugins are included in the published
application. Users do not install NAudio, LibreHardwareMonitor, HidSharp, or
System.Drawing.Common separately.

## Setup choices

Setup asks whether Desktop Shrine should start when the current user signs in.
It also asks whether the host should run as administrator; this defaults to yes
because LibreHardwareMonitor can return incomplete sensor data without elevated
hardware access.

When administrator mode is selected, setup creates an elevated scheduled task
triggered at user logon. When user mode is selected, it creates a normal
per-user startup entry. Both modes stay in the interactive desktop session and
show no console window. The development-only Console Display plugin is not
included in release packages.

Writable configuration is stored in:

```text
%LOCALAPPDATA%\DesktopShrine\configuration
```

Application binaries and plugins are installed beneath Program Files. Removing
Desktop Shrine removes its GOverlay bridge but deliberately leaves the official
GOverlay application installed, since it may be used independently.

## Legacy GOverlay compatibility and preservation

Desktop Shrine can optionally download the legacy GOverlay installer directly
from the official GOverlay website. This option is provided solely for
compatibility with discontinued GOverlay display hardware.

GOverlay is third-party software and is not included in the Desktop Shrine
distribution. Desktop Shrine does not host, modify, or redistribute the
GOverlay installer. Availability depends on the official GOverlay download
remaining accessible.

All GOverlay names, software, trademarks, and associated rights remain the
property of their respective owner. No ownership, endorsement, or affiliation
is claimed.

The Desktop Shrine GOverlay bridge and plugin are separate integration
components developed as part of this repository under the project's MIT
licence. They allow Desktop Shrine to communicate with the third-party
GOverlay application; they are not the GOverlay application or installer. See
the complete [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md).

## Building the installer

Run:

```bat
build-installer.cmd
```

The build machine needs:

- the .NET 10 SDK;
- Inno Setup 6 (the batch file offers to install it with `winget` when absent);
- an existing legacy GOverlay installation containing `Interfaces.dll`, used
  only to compile the bridge against the official SDK;
- internet access for the first NuGet restore.

The script runs the Release test suite, publishes a self-contained `win-x64`
host, publishes all production plugins except Console Display, builds the
legacy GOverlay bridge, and compiles the final installer into:

```text
artifacts\installer\output
```

The output directory contains the setup executable and a matching
`.exe.sha256` file. The hash file records the setup executable's SHA-256 digest
for release verification.

The generated setup executable is unsigned unless a code-signing step is added
for your certificate. Windows may therefore show an Unknown publisher or
SmartScreen prompt when the installer is shared with another computer.

To choose a four-part installer version or skip tests during a packaging-only
iteration, call the PowerShell entry point directly:

```powershell
.\scripts\Build-WindowsInstaller.ps1 -Version 1.1.0
.\scripts\Build-WindowsInstaller.ps1 -SkipTests
```
