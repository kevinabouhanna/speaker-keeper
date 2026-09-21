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

### Fixed

- **Two speakers of the same model can be told apart.** A stereo pair is two identical
  speakers, so both announce the same name, and Settings showed two identical rows with a
  switch each and no way to know which was which. The last four digits of the Bluetooth
  address now follow the name, and only when there is a clash, so a single speaker still
  reads as itself.

## [1.5.0] - 2026-09-21

### Added

- **Speaker Keeper now tells you when the problem is not Speaker Keeper.** A Bluetooth
  speaker that keeps dropping its connection looks exactly like a speaker idling off: it
  goes quiet, and the app meant to prevent that gets the blame. When a speaker disconnects
  three or more times in an hour, the app now says so by name and with a count, and names
  the most likely cause on your machine: a second Bluetooth adapter, which Windows will
  not use (it only ever drives one, chosen at startup), or an adapter whose driver has
  failed. Settings carries the same text for as long as it is true. Nothing is changed for
  you: removing a Bluetooth adapter can take a mouse or keyboard with it.
- **Settings lists your Bluetooth adapters** when there is more than one, with the driver
  problem code for any that is not working.

### Fixed

- **A speaker paired to two Bluetooth adapters is no longer held awake twice.** It can
  publish a separate output on each adapter, and both were being kept alive. The one
  Windows is actually playing through wins.
- **A stream that cannot start is no longer retried every five seconds forever.** If
  another app holds the output exclusively, or a driver refuses it, the wait now doubles
  from 5 seconds up to 5 minutes and resets the moment the stream comes up. It was
  rebuilding the stream and writing a log line every tick, indefinitely.
- **Speakers on an unrecognised Bluetooth stack are kept awake again.** 1.4.0 identified
  a speaker's music channel by its position in the device tree, and anything that did not
  match was skipped as if it were the call channel. A speaker that publishes no channel
  the app recognises is now kept awake rather than silently ignored, while the call
  channel is still never touched.
- **Three error descriptions in the log were wrong.** `0x88890001` and `0x8889000A` were
  labelled as exclusive-mode and audio-service failures; they are actually
  `NOT_INITIALIZED` and another app holding the output exclusively, and the audio service
  one is `0x88890010`.
- **The status no longer says "Idle" while speakers are being kept awake.** With more than
  one speaker, the tray panel and the log both reported on the current output alone, so
  the app looked idle while it was holding another speaker awake in the background.

## [1.4.0] - 2026-09-21

### Added

- **Every speaker you own is kept awake, not just the one you are listening to.** Until
  now only the current output was held, so the other speaker in the room went to sleep
  and switching to it brought back exactly the delay this app exists to remove. Each
  connected speaker you have switched on is now held awake at the same time, so moving
  between them is instant in both directions.
- **Speakers you have paired but never connected are listed in Settings**, so a second
  speaker can be switched on before the first time you use it rather than after.

### Fixed

- **Earbuds and headsets really are left alone now.** The app said it only worked on
  speakers, and then held a silent stream open on any Bluetooth output that became the
  default, headphones included, which flattens them in a bag. It now asks the device what
  it is: speakers are kept awake, earbuds and headsets are left to sleep, and the switch
  in Settings still overrides it for a speaker that reports itself wrongly.
- **A speaker's call channel is never held awake.** One Bluetooth speaker publishes two
  outputs: the stereo one you listen through and a mono hands-free one for calls. Only
  the first is held now, so nothing the app does can put a speaker into call mode.
- **The log says what actually happened.** A Bluetooth output being torn down underneath
  a running stream was recorded as a bare `silent stream failed: Not implemented`, with
  no error code and no way to tell a speaker disconnecting apart from a driver refusing
  the stream. Lines now name the speaker and carry the real code, with the common ones
  spelled out, so `the speaker disconnected` reads as what it is.

## [1.3.2] - 2026-09-20

### Changed

- **The battery threshold in Settings now says what it is.** The *Warn below* row was a
  number with no icon and no explanation, sitting under the low-battery switch without
  saying it belonged to it. It has a battery icon and a line telling you what the number
  does.
- **The project link on the About page uses the app's own speaker icon** rather than a
  Bluetooth one, matching everywhere else that stands for Speaker Keeper itself.

## [1.3.1] - 2026-09-19

### Fixed

- **The leftover `silent.wav` is now cleared from machines that update in the
  background.** 1.3.0 stopped using the file and the installer deletes it, but a
  background update replaces only the program itself, so anyone who updated overnight
  was left with 3.4 MB of dead weight in their install folder and no way to be rid of it
  short of reinstalling. The updater now tidies the install folder on every check, and
  the installer does the same, so this and the `.old` copies left behind by previous
  updates clear themselves.

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

- **`silent.wav` is gone.** The app makes its own silence now, so the 3.4 MB file the
  installer used to write is no longer needed. Running the installer again removes it.
  A background update will not: those replace only the program itself, so a machine that
  updated overnight keeps the stray file until it is reinstalled.

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

[Unreleased]: https://github.com/kevinabouhanna/speaker-keeper/compare/v1.5.0...HEAD
[1.5.0]: https://github.com/kevinabouhanna/speaker-keeper/compare/v1.4.0...v1.5.0
[1.4.0]: https://github.com/kevinabouhanna/speaker-keeper/compare/v1.3.2...v1.4.0
[1.3.2]: https://github.com/kevinabouhanna/speaker-keeper/compare/v1.3.1...v1.3.2
[1.3.1]: https://github.com/kevinabouhanna/speaker-keeper/compare/v1.3.0...v1.3.1
[1.3.0]: https://github.com/kevinabouhanna/speaker-keeper/compare/v1.2.1...v1.3.0
[1.2.1]: https://github.com/kevinabouhanna/speaker-keeper/releases/tag/v1.2.1
[1.2.0]: https://github.com/kevinabouhanna/speaker-keeper/releases/tag/v1.2.0
[1.1.0]: https://github.com/kevinabouhanna/speaker-keeper/releases/tag/v1.1.0
[1.0.0]: https://github.com/kevinabouhanna/speaker-keeper/releases/tag/v1.0.0
