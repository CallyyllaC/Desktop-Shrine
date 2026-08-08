# Desktop Shrine

> [!Warning]
> There are currently issues using some GOverlay devices, either IPS or later (251) firmware is causing it to crash and go into and infinite boot loop. I am working on a fix.

Desktop Shrine is a modular Windows desktop runtime that turns activity on the computer into ambient visual output.

It can react to music, games, desktop audio, and hardware activity, then present that information through devices such as a GOverlay LCD screen or a BlinkStick RGBW LED strip. Inputs and outputs are implemented as plugins, allowing the system to grow without turning the host application into one enormous switch statement.

> [!NOTE]
> Desktop Shrine is personal project, some setup or hardware-specific configuration may be required.

## Current features

### Inputs

- **Windows Now Playing** reads media information exposed through Windows media controls.
- **Steam Now Playing** detects the currently running Steam game and can load local artwork and optional store metadata.
- **Audio Collector** captures desktop audio or an input device and produces spectrum data for visualisers.
- **Hardware Monitor** uses LibreHardwareMonitor to provide CPU, GPU, temperature, power, fan, clock, and memory telemetry when supported by the system.

### Intermediaries

- **Artwork Palette** extracts a small colour palette from album or game artwork for use by visual outputs.

### Outputs

- **GOverlay LCDSysInfo** displays media, game, and hardware information on a supported GOverlay screen.
- **BlinkStick RGBW** provides audio-reactive and hardware-reactive LED effects.
- **Console Display** exposes plugin activity and state during development.

## How routing works

Each output has its own ordered list of enabled inputs. The highest-priority active input is selected independently for that output.

For example, an LED strip may use the audio spectrum and artwork palette while media is playing, then fall back to a hardware activity display when the media input becomes inactive. A separate idle-animation plugin can sit at the bottom of the priority list.

Routing profiles are stored in:

```text
configuration/output-input-profiles.json
```

Changes are validated and published to running outputs without requiring the entire host to restart.

## Running the project

Desktop Shrine targets **.NET 10** and is currently intended primarily for Windows.

Build and test the solution with:

```bash
dotnet test DesktopShrine.slnx
```

For local development, run the `DesktopShrine.Host` project. Debug builds automatically stage the bundled contracts and plugins beside the host.

Once running, start media through a Windows-compatible player or launch a Steam game. The console output can be used to verify discovered plugins, published state, audio data, artwork palettes, and output routing.

## Windows installer

Run `build-installer.cmd` to test and publish a self-contained Release build and
compile the Windows installer. Release packages run silently in the signed-in
desktop session, exclude the development Console Display plugin, and do not
require users to install the .NET 10 runtime separately.

Setup offers startup with Windows and administrator mode (recommended for
complete hardware telemetry), plus the option to download and launch the
official legacy GOverlay 1.6.9 installer and install the Desktop Shrine bridge.
See [`docs/installation.md`](docs/installation.md) for the complete dependency
and build requirements.

### GOverlay setup

After installing GOverlay and the Desktop Shrine bridge:

1. Open GOverlay and enable the Desktop Shrine plugin.
2. Create a new GOverlay profile for Desktop Shrine.
3. Add the Desktop Shrine plugin to the new profile.
4. Upload the bundled `Oxanium-Bold_20px.bin` font to GOverlay.
5. Select the new profile and start Desktop Shrine.

The GOverlay output should then be available to display media, game, and
hardware information published by Desktop Shrine.

`DontUseDrawPixels` in `configuration/plugins/goverlay.json` defaults to `true`.
In that mode album artwork is prepared as a bounded adaptive set of filled
rectangles; all other drawing uses the normal renderer. Set it to `false` for a
device known to support the original `LCDSys2_Draw_Pixels` artwork transfer.

## Configuration

Each plugin has a flat JSON configuration file:

```text
configuration/plugins/<plugin-id>.json
```

When a plugin is discovered without a matching file, the host creates an empty configuration file so its settings have an obvious home.

Configuration values can also be overridden through normal .NET configuration sources. For example:

```text
Plugins__audio-collector__FramesPerSecond=30
```

This overrides:

```text
Plugins:audio-collector:FramesPerSecond
```

### Audio capture

The Audio Collector captures the default Windows desktop output at 50 frames per second by default. To capture a microphone or another input device instead, change `CaptureMode` in:

```text
configuration/plugins/audio-collector.json
```

### Steam integration

The Steam input reads the running AppID, discovers installed Steam libraries, and resolves the matching application manifest.

Optional artwork and Steam store metadata can be enabled in:

```text
configuration/plugins/steam-now-playing.json
```

Artwork is resolved from local Steam files where possible and can optionally fall back to downloaded or executable artwork. Cached data is stored beneath the current user's local application data directory unless another cache location is configured.

## BlinkStick visualiser

While media is active, each audio frequency band is mapped through colours extracted from the current artwork. Low-energy bands use the darker palette colours, while stronger peaks lerp toward the brighter colours.

When hardware monitoring is selected, the strip is divided at its centre:

- GPU activity moves outward across one half.
- CPU activity moves outward across the other.
- Workload affects brightness and movement.
- Power draw affects trail length.
- Temperature shifts the effect from cool tones toward warning colours.

The development defaults target 24 RGBW pixels on BlinkStick Pro channel 0, render at 20 frames per second, and enforce a conservative USB power budget. Separately powered strips can be configured explicitly.

BlinkStick settings are stored in:

```text
configuration/plugins/blinkstick-bar.json
```

## Plugin layout

The host discovers:

- passive contract assemblies from `contracts`
- active plugin assemblies from `plugins`

Sample deployment manifests live alongside the sample projects.

The architecture follows a ports-and-adapters approach: plugins communicate through shared contracts, while device-specific or platform-specific code remains at the edges of the system.

## Licence

Desktop Shrine is licensed under the [MIT License](LICENSE). Third-party
components and assets retain their respective licences; see
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) for details.
