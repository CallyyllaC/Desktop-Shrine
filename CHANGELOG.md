# Changelog

This changelog records Desktop Shrine changes since the `1.1.0` GitHub
release. Versions after `1.1.0` were local/internal builds and had not been
pushed as GitHub releases when `2.0.0-alpha` was prepared.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [2.0.0-alpha] - 2026-08-22 — Current unreleased build

### Changed

- Replaced frame-luminance-based perceptual gamma compensation with a fixed
  output order: effect RGBW, user gamma, user brightness, electrical limit,
  clamp, and device quantisation.
- Brightness now proportionally scales the selected gamma-shaped output;
  changing gamma is allowed to change perceived brightness naturally.
- Removed the perceptual compensation toggle, strength, multiplier bounds,
  smoothing, and luminance-floor settings. Existing persisted keys are ignored
  safely for compatibility.
- Updated application, tray, settings, plugin, and installer presentation to
  `2.0 Alpha`; installer artifacts use the `2.0.0-alpha` suffix.

### Fixed

- Restored dragging and double-click maximise/restore on the custom settings
  title bar.
- Prevented the hidden native WinForms scrollbar from flashing while the
  branded settings scrollbar is used.

## [1.4.0-beta] - 2026-08-22 — Internal build

### Added

- Added the full Desktop Shrine settings window, opened by double-clicking the
  notification-area icon.
- Added plugin-owned settings metadata, shared live configuration editing, and
  restart-required explanations.
- Added persistent Quick Access selection for up to five suitable controls.
- Added application, configuration, log, plugin, and data-folder shortcuts.

### Changed

- Replaced hard-coded BlinkStick/audio tray controls with dynamically rendered
  Quick Access controls.
- Added custom dark title-bar and scrollbar styling consistent with the tray.
- Displayed effective resolved defaults for lazily created Steam cache folders.
- Added the product version to the settings and tray headers and removed the
  redundant `Running` label.

## [1.3.7] - 2026-08-22 — Internal build

### Fixed

- Made Windows audio endpoint enumeration tolerate partial COM/property-store
  failures and merge sparse MTA results with a tray-apartment retry.
- Preserved the synthetic default entry while still exposing all usable render
  or capture endpoints by stable device ID.

## [1.3.6] - 2026-08-22 — Internal build

### Fixed

- Added fallback audio enumeration for elevated/session apartment combinations
  where the initial endpoint query returned only the default device.
- Improved handling of temporarily missing endpoints and default-device changes.

## [1.3.5] - 2026-08-21 — Internal build

### Changed

- Redesigned the tray popup as a dark Desktop Shrine panel with the project
  torii branding, cyan/fuchsia accents, custom sliders, and styled action rows.
- Replaced the native audio combo box with a themed device flyout.

### Fixed

- Corrected sticky hover/focus styling in tray controls.

## [1.3.4] - 2026-08-21 — Internal build

### Fixed

- Corrected restart process handoff so Restart launches a replacement process
  after the shared clean-shutdown path completes.
- Expanded audio endpoint discovery beyond the default device and retained
  saved preferences when an endpoint temporarily disappears.

## [1.3.3] - 2026-08-21 — Internal build

### Added

- Added shared BlinkStick output-stage gamma, brightness, and electrical-limit
  processing for audio/Steam and telemetry effects.
- Added deterministic application shutdown coordination plus tray Restart and
  Exit actions.
- Added explicit BlinkStick clear/off delivery before device disposal.
- Added stable audio endpoint selection and live capture rebinding.

### Changed

- Added gamma-aware luminance compensation as an initial experiment; this
  behavior and its settings were subsequently removed in `2.0.0-alpha`.

## [1.3.2] - 2026-08-18 — Internal build

### Added

- Added audio source/capture mode selection and BlinkStick gamma to the tray.
- Added runtime-safe live updates for BlinkStick and audio endpoint settings.

### Changed

- Restyled sliders and selectors to better match the Desktop Shrine theme.

### Fixed

- Fixed a crash when opening the audio device selector.

## [1.3.1] - 2026-08-18 — Internal build

### Changed

- Replaced the brightness percentage list with a slider.
- Added exact 1% brightness steps with logarithmic slider travel while keeping
  stored brightness values linear.

## [1.3.0] - 2026-08-18 — Internal build

### Added

- Added the Taskbar Controls output plugin and Desktop Shrine notification-area
  icon.
- Added right-click BlinkStick brightness control.
- Applied the Desktop Shrine torii artwork to the tray, executables, and
  installer.

## [1.1.0] - 2026-08-08 — Last GitHub release

### Changed

- Replaced the default GOverlay pixel artwork renderer with the adaptive
  rectangle-shape renderer.
- Improved GOverlay IPS-device stability.

