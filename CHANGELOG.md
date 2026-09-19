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

## [1.3.0] - 2026-09-19

### Added

- **Click the tray icon once and a panel opens.** It shows which speaker you are on,
  whether it is being held awake, and its battery, with a switch to turn Speaker Keeper
  off for that speaker. It sits above the tray like the volume and brightness panels do,
  and closes as soon as you click away.

### Changed

- **Speaker Keeper no longer shows up in Windows' own audio controls.** It used to
  appear as a media card with play, pause, next and previous buttons (buttons for a
  track that does not exist) and as a slider in the volume mixer. Pressing pause there
  actually stopped the keep-alive until the app noticed and restarted it. Both are gone:
  the app now feeds silence straight to the speaker instead of pretending to be a music
  player, so Windows has nothing to show.
- **Settings has been rebuilt to look like Windows 11.** Grouped pages instead of one
  dialog of checkboxes, with a line under each setting explaining what it actually does.
- **The whole app follows your Windows theme.** Light or dark, plus your accent colour,
  across the panel, Settings, the log window and the installer. It changes over the
  moment you switch Windows, without being restarted.
- **The installer looks like the rest of the app now,** rather than a grey dialog from
  2001. Same wording and the same steps; nothing about what it installs has changed.
- **Double-clicking the tray icon no longer does anything special.** One click opens the
  panel instead. The right-click menu is now just Settings and Quit, because everything
  it used to list is on the panel.

### Removed

- **`silent.wav` is gone.** The app generates its silence as it goes, so the 3.4 MB file
  the installer used to write is no longer needed. Existing installs have it cleaned up
  automatically, whether they update in the background or you reinstall over the top.

## [1.2.1] - 2026-09-19

### Changed

- **The icon is now blue instead of purple.** Speaker Keeper is a Bluetooth app, so it
  may as well look like one. The tray icon, the shortcut, the installer and the Settings
  window all pick the new one up. Nothing about how it behaves has changed.

### Fixed

- Git was rewriting line endings inside the icon and logo files, corrupting them. Added
  `.gitattributes` so image files are treated as binary.

## [1.2.0] - 2026-09-19

### Added

- **Setup can now turn on automatic updates**, ticked by default. Previously the only
  way to enable them was in Settings, which needs a separate Windows permission prompt;
  setup is already running with permission, so it costs nothing there. `Install.exe`
  takes `/NOAUTOUPDATE` for managed deployments that patch on their own schedule.
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

### Fixed

- Turning on *Install updates automatically* in Settings and then dismissing the Windows
  permission prompt used to reset the checkbox with no explanation, which looked exactly
  like the setting refusing to stick. It now says what happened and why nothing changed.

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

[Unreleased]: https://github.com/kevinabouhanna/speaker-keeper/compare/v1.3.0...HEAD
[1.3.0]: https://github.com/kevinabouhanna/speaker-keeper/compare/v1.2.1...v1.3.0
[1.2.1]: https://github.com/kevinabouhanna/speaker-keeper/releases/tag/v1.2.1
[1.2.0]: https://github.com/kevinabouhanna/speaker-keeper/releases/tag/v1.2.0
[1.1.0]: https://github.com/kevinabouhanna/speaker-keeper/releases/tag/v1.1.0
[1.0.0]: https://github.com/kevinabouhanna/speaker-keeper/releases/tag/v1.0.0
