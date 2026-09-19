# Changelog

All notable changes to Speaker Keeper are recorded here.

Updates install silently in the background, so this file is how you find out what
changed on your machine. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

<!--
Add entries under Unreleased as you go. At release time that whole section moves
down under a new version heading - see docs/RELEASING.md. The release workflow reads
the notes for a version straight out of this file, so the heading format matters:

  ## [1.2.0] - 2026-10-04

Categories, in this order, omitting any that are empty:
Added / Changed / Fixed / Removed / Security
-->

## [Unreleased]

Nothing yet.

## [1.2.0] - 2026-09-19

### Added

- The version you are running is now shown in Settings, with a **What's new** link to
  the release notes for that exact version.
- A notification when an update has been installed, since updates land silently
  overnight. It appears once, on the first launch after the version changes, and opens
  the release notes when clicked.
- `verify.ps1`, which checks a build is internally consistent before it is released:
  matching versions across all three exes, `Install.exe` carrying exactly the binaries
  that sit beside it, every embedded resource present, and a changelog entry for the
  version being shipped.

### Changed

- The release workflow now refuses to publish a manifest whose version doesn't match the
  version stamped into the binaries. Getting that wrong made every installed copy
  reinstall the same build nightly, forever, and nothing caught it before.

## [1.1.0] - 2026-09-19

### Added

- **`Install.exe` - a normal Windows installer.** Download one file, double-click it,
  approve the prompt, follow the wizard. It carries everything it needs inside itself.

### Changed

- The update feed is now configured automatically at install time. In 1.0.0 no feed was
  set, so ticking *Install updates automatically* silently did nothing.

### Removed

- `install.ps1`. Telling people to right-click a script and *Run with PowerShell* did not
  work: it doesn't request administrator rights, the option isn't registered on every
  machine, and Windows refuses to run downloaded scripts that aren't signed. New users
  got an editor window or a security error instead of an install. `Install.exe /S` covers
  everything the script did.

## [1.0.0] - 2026-09-19

First public release.

### Added

- Keeps a Bluetooth speaker awake by holding a silent media session open, so it stops
  powering itself off and cutting the start off your music.
- Battery level for the connected speaker, in the tray tooltip and menu, with a
  low-battery warning.
- Skips earbuds and headphones deliberately, so they still power themselves off.
- Leaves media keys alone - play/pause and skip keep controlling your music apps.
- Follows the default output device when you switch speakers.
- Per-speaker on/off, a live log viewer, start-with-Windows, and opt-in auto-updates.

[Unreleased]: https://github.com/kevinabouhanna/speaker-keeper/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/kevinabouhanna/speaker-keeper/releases/tag/v1.2.0
[1.1.0]: https://github.com/kevinabouhanna/speaker-keeper/releases/tag/v1.1.0
[1.0.0]: https://github.com/kevinabouhanna/speaker-keeper/releases/tag/v1.0.0
