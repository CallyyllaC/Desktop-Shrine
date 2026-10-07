# Third-party notices

## Oxanium

Oxanium is distributed under the SIL Open Font License, Version 1.1.

Original copyright notice:

> Copyright 2019 The Oxanium Project Authors (https://github.com/sevmeyer/oxanium)

`Oxanium-Bold_20px.bin` is a converted binary font asset used by the Desktop
Shrine GOverlay integration. The original font and the converted font asset
remain subject to the SIL Open Font License 1.1; they are not owned or
relicensed by the Desktop Shrine project. The complete licence is included in
[`third-party-licenses/Oxanium-OFL-1.1.txt`](third-party-licenses/Oxanium-OFL-1.1.txt).

## Distributed runtime and package dependencies

Desktop Shrine's Windows installer is self-contained and redistributes the
following runtime and package components. They are dependencies only: their
licences do not replace Awoo Licence v2.0 for project-owned code.

### .NET 10

The self-contained application includes the .NET runtime, ASP.NET Core,
Windows Desktop, and supporting `System.*` and `Microsoft.*` libraries. These
components are provided under their applicable Microsoft and .NET Foundation
terms, primarily the MIT License, together with their own third-party notices.

The installer build copies the exact `LICENSE.txt` and
`ThirdPartyNotices.txt` supplied by the .NET toolchain used for that build into
`licenses/third-party-licenses/`. The `System.Drawing.Common` package's
additional notices are preserved in
[`third-party-licenses/System.Drawing.Common-THIRD-PARTY-NOTICES.txt`](third-party-licenses/System.Drawing.Common-THIRD-PARTY-NOTICES.txt).

Source: <https://github.com/dotnet/dotnet>

### LibreHardwareMonitor and its MPL dependencies

The hardware-monitor plugin redistributes these unmodified MPL-2.0 packages:

- LibreHardwareMonitorLib 0.9.6
- BlackSharp.Core 1.0.7
- DiskInfoToolkit 1.1.2
- RAMSPDToolkit-NDD 1.4.2

Their source code at the exact commits identified by the NuGet packages is
available from:

- <https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701>
- <https://github.com/Blacktempel/BlackSharp/tree/c70b735c6cec123ee8a046ac4a0bc6c606f52cf0>
- <https://github.com/Blacktempel/DiskInfoToolkit/tree/25319eae5781e75bcf141e844ceab2afe94d40ea>
- <https://github.com/Blacktempel/RAMSPDToolkit/tree/3b47b960e0830fef344624ad5e389675d5f0a1ce>

The complete MPL-2.0 text is included in
[`third-party-licenses/MPL-2.0.txt`](third-party-licenses/MPL-2.0.txt).

### NAudio

NAudio.Core 2.2.1 and NAudio.Wasapi 2.2.1 are distributed under the MIT
License. Their copyright notice and licence are included in
[`third-party-licenses/NAudio-MIT.txt`](third-party-licenses/NAudio-MIT.txt).

Source: <https://github.com/naudio/NAudio/tree/v2.2.1>

### HidSharp

HidSharp 2.6.4 is distributed under the Apache License 2.0. Its package
copyright notice and complete licence are included in
[`third-party-licenses/HidSharp-Apache-2.0.txt`](third-party-licenses/HidSharp-Apache-2.0.txt).

Project: <https://software.seekye.com/hidsharp>

### Mono.Posix.NETStandard

Mono.Posix.NETStandard 1.0.0 and its native helper are redistributed through
LibreHardwareMonitor. The complete licence and component notices published by
the Mono project are included in
[`third-party-licenses/Mono-LICENSE.txt`](third-party-licenses/Mono-LICENSE.txt).

Source: <https://github.com/mono/mono>

## GOverlay

### Third-party GOverlay application and installer

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

### Desktop Shrine GOverlay bridge and plugin

The Desktop Shrine GOverlay bridge and plugin in this repository are developed
as part of Desktop Shrine and are covered by the project's
[`Awoo Licence v2.0`](LICENSE). They are separate integration components that allow
Desktop Shrine to communicate with the third-party GOverlay application. Their
inclusion does not imply ownership of, endorsement by, or affiliation with
GOverlay or its owner.
