# Speaker Keeper

Keeps a Bluetooth speaker from powering itself down during silence, so it behaves
like a wired speaker that's simply always on.

## How it works

Bluetooth speakers idle-off when no audio stream is present. Speaker Keeper holds a
real Windows media session open: it loops `silent.wav` through a WinRT `MediaPlayer`,
which keeps an audio stream alive on the default output endpoint without making a sound.

Two details matter:

- **Transport controls are suppressed.** The session registers with SMTC (that's what
  keeps the speaker awake), but next/previous/seek/shuffle/repeat are disabled so your
  media keys still reach Spotify, YouTube, etc. Play and Pause are deliberately left at
  their defaults — disabling those makes Windows drop the session entirely, which defeats
  the whole point.
- **It follows the default output device.** A 5-second timer polls the default endpoint
  via `IMMDeviceEnumerator` and rebuilds the player when you switch outputs, and also
  reasserts playback if anything stops it.

A named mutex (`Local\SpeakerKeeperSMTC`) prevents a second instance.

## Tray icon

Speaker Keeper sits in the notification area.

- **Double-click** opens Settings — the usual Windows convention for a tray app.
- **Right-click** opens the menu:
  - **Output** — the current default playback device
  - **Battery** — charge level of that device (`(charging)` if rising), or `not reported`
  - **Status** — `keeping awake`, or `idle` with the reason
  - **Start with Windows** — toggles the autostart entry
  - **Settings…** — low-battery warning, per-speaker control, live log, updates
  - **Quit** — stops the silent loop and exits, letting the speaker idle off normally

Both readings refresh whenever the menu opens, and the tooltip shows the battery level
on hover. Clicking either row forces a re-read.

## Which devices get kept awake

Two gates, both in `ShouldKeepAwake`:

1. **It must be a Bluetooth output.** Monitors, USB and analogue devices never idle off,
   so holding a stream open on them achieves nothing. Worse, holding *earbuds* awake stops
   them auto-powering-off and drains their battery. Anything non-Bluetooth is left alone.
2. **That speaker must not be switched off** in Settings → *Keep these speakers awake*.

When either gate fails the silent session is stopped and the tray reports
`Status: idle - <reason>`. The reason is logged, so it's never a mystery.

Settings are stored per physical device under:

```
HKCU\Software\SpeakerKeeper\Devices\{container-guid}
    Enabled : REG_DWORD   1 = keep awake
    Name    : REG_SZ      for display when the speaker is disconnected
```

Keyed by **ContainerId**, not endpoint id: an endpoint id changes when a device is
re-paired, but the container is stable, so a speaker keeps its setting. New Bluetooth
speakers default to enabled.

The list shows connected Bluetooth speakers plus any seen before, marked
`(not connected)`, so a speaker that is currently switched off can still be configured.

> One speaker publishes several endpoints — A2DP (`Speakers (X)`), Hands-Free
> (`Headset Earphone (X Hands-Free)`), AVRCP — which all share a container. The list is
> deduplicated by container and named from the Bluetooth device root node, so you see
> *Xiaomi Sound Pocket* once rather than one row per profile.

## Charging detection

**Windows does not expose charging state for Bluetooth audio devices.** Probing every
property on the node that reports battery turns up only two:

| Property | Meaning |
|---|---|
| `{104EA319-6EE2-4701-BD47-8DDBF425BBE5}` PID 2 | battery percentage |
| `{104EA319-6EE2-4701-BD47-8DDBF425BBE5}` PID 7 | when that reading was last refreshed |

There is no charging flag, and there is nowhere for one to come from: the HFP battery
indicator carries a level only, and the GATT Battery Service (0x180F) defines just
Battery Level.

### The inference is deliberately one-sided

The obvious rule — *rising means plugged in, falling means unplugged* — is **wrong**, and
wrong in a way that matters. The two directions are not symmetric:

- **A battery cannot gain charge on its own.** A rising percentage therefore means
  external power, full stop. This is sound.
- **A falling percentage proves nothing about the cable.** It only means draw exceeded
  supply. A speaker that is being held awake — which is exactly what this app does — or
  playing above about half volume can drain *while plugged in*. On the Xiaomi Sound
  Pocket the discharge rate at high volume exceeds its charge rate outright.

So the app asserts charging **only** on a rise, and never claims a device is unplugged:

```
on charger - battery rose 80% -> 90% on Speakers (Xiaomi Sound Pocket)
battery draining (cannot tell if plugged in) on Speakers (Xiaomi Sound Pocket)
battery 100% -> 90% (-10% in 33m, ~18%/h)
```

The rate is logged so the trend can be judged rather than guessed at: a speaker shedding
2%/h is almost certainly on a charger losing the race, while one shedding 20%/h is not.
That call is left to the reader, because no threshold is right for every speaker.

Remaining limits:

- A speaker sitting at 100% on a charger never changes, so charging can't be confirmed.
- Readings move in 10% steps and are **pushed by the device**, not polled — the `PID 7`
  timestamp is often 10+ minutes old — so transitions surface on that delay.
- Plugging and unplugging between two reports is invisible.

### Note on the app's own effect

Keeping a speaker awake is not free. The point of this app is that the speaker never
idles off, which means its amplifier stays powered continuously. On a weak charging port
that draw can be enough to hold the battery flat or slowly falling even while plugged in,
so a device that used to sit at 100% may settle lower once this app is running. That is
expected, not a fault — and it is another reason a falling battery says nothing about
whether the cable is connected.

## Live log

**Settings → View log** opens a tailing viewer rather than dumping the file into Notepad,
so you can watch the app react to device changes as they happen. It polls once a second
and appends only what is new.

- **Follow live** (on by default) — unticking freezes the view; re-ticking catches up,
  since the read position isn't advanced while frozen.
- **Open in Notepad** — the raw file, if you want to search or keep it.

The file is opened with `FileShare.ReadWrite`, because the app is appending to it while
the viewer reads. Only the last 256 KB is loaded on open; if the file is truncated or
rotated underneath, the viewer notices the length going backwards and reloads.

### What gets logged, and why

The log exists to answer one question: *when the speaker did fall asleep, what happened?*
Each entry is there because it narrows that down.

| Entry | Why it matters |
|---|---|
| `--- starting vX, pid, exe` | Version and path — the first thing to establish in any bug report |
| `player started` | The silent session was created successfully |
| `start failed: …` | It wasn't — the reason follows |
| `state=X - reasserting play` | Something stopped playback; the app noticed and pushed back |
| `still not playing - rebuilding` | Reasserting wasn't enough, so the player was rebuilt |
| `default output changed -> NAME` | You switched devices, **and which one** — a very common cause of confusion |
| `stopping - <reason>` / `idle - <reason>` | Deliberately not keeping this output awake, and why |
| `on charger - battery rose …` | A rise was seen, which only external power can cause |
| `battery draining (cannot tell if plugged in)` | Falling — says nothing about the cable |
| `battery X% -> Y% (… ~N%/h)` | Rate of change, for judging the trend yourself |
| `power mode: Suspend` / `Resume` | The machine slept. This is the single most common point of failure |
| `post-resume check - output is NAME` | What the audio stack looked like 5s after waking |
| `session ending: …` | Logoff/shutdown, so an abrupt stop isn't mistaken for a crash |
| `battery N% on NAME` | Discharge curve, logged at 10% steps |
| `low battery warning at N%` | A toast was raised (or silently dropped — see below) |
| `heartbeat state=Playing` | Every 10 min; proves the app was alive and healthy between events |
| `tick error` / `menu refresh failed` / `battery check failed` | Unexpected failures, with the exception message |
| `quit requested from tray` | The user stopped it deliberately |

The log rotates at 1 MB to `SpeakerKeeper.log.old`, so it can't grow without bound on a
machine that's always on.

## Low-battery notification

When the speaker drops below the threshold (default 20%), Speaker Keeper raises a
standard Windows notification. It uses `NotifyIcon.ShowBalloonTip`, which the Windows
10/11 shell renders as a real system toast — so it obeys Do Not Disturb and the normal
per-app notification settings rather than drawing anything custom.

It warns **once** per discharge. The latch only resets once the battery climbs 5 points
back above the threshold, so a reading sitting on the boundary won't nag.

> **Notifications must be enabled system-wide for this to appear.**
> If `HKCU\Software\Microsoft\Windows\CurrentVersion\PushNotifications\ToastEnabled`
> is `0` (Settings → System → Notifications turned off), no app can raise a toast and
> the warning is silently dropped. The battery reading in the menu still works.

Battery is polled every 2 minutes rather than on every 5-second tick, since each read
walks the device tree and the value moves slowly.

## Settings

Stored in `HKCU\Software\SpeakerKeeper`, so they survive the folder being moved:

| Value | Meaning |
|---|---|
| `WarnLowBattery` | `1`/`0` — whether to raise the low-battery toast |
| `LowBatteryThreshold` | Percentage to warn below (clamped 5–95, default 20) |

The key is only created once you change something; until then the defaults apply.

### How the battery reading works

The audio endpoint (`SWD\MMDEVAPI\...`) is not the devnode that reports battery — that
lives on the Hands-Free AG node under `BTHENUM`. The two are linked by a shared
`ContainerId`, so Speaker Keeper reads the endpoint's container, then scans `BTHENUM`,
`BTHLE` and `BTHHFENUM` for a node with the same container and a populated
`DEVPKEY_Bluetooth_Battery`. That means the reading follows whichever device is
currently the default output, rather than being hardcoded to one speaker.

Devices that don't report battery over Bluetooth show `not reported`; this is a
limitation of the device, not of the lookup.

### Making the icon always visible

Windows 11 hides new tray icons in the overflow flyout by default. To pin it: drag it out
of the `^` overflow, or set `IsPromoted` to `1` under the app's subkey in
`HKCU\Control Panel\NotifyIconSettings\` and restart Explorer.

Note that Windows stores the path there with a KNOWNFOLDERID prefix
(`{6D809377-...}\Speaker Keeper\SpeakerKeeper.exe`), not a literal path, which is why the
uninstaller matches on trailing path segments rather than the whole string.

## Icons

`SpeakerKeeper.ico` carries nine natively-rendered frames — 16, 20, 24, 32, 40, 48, 64,
128, 256 — so nothing is ever upscaled. Small sizes are classic DIBs, 48 and above are
PNG-compressed.

Two different icons are loaded at runtime, and mixing them up is what makes an icon look
blurry:

- `TrayIcon()` → `new Icon(path, SystemInformation.SmallIconSize)`, a single 16×16 frame
  for `NotifyIcon`.
- `WindowIcon()` → `new Icon(path)`, the **whole** multi-resolution icon for `Form.Icon`.

Handing the 16×16 tray icon to a Form makes the taskbar stretch 16px up to 32px, which
looks soft. The Form must get the full icon so Windows can pick the right frame per
context.

The manifests also set `dpiAware`, without which Windows bitmap-stretches the entire
window on scaled displays and everything — text included — goes blurry.

## Files

| File | Purpose |
|---|---|
| `SpeakerKeeper.exe` | The app. No window; lives in the tray. |
| `SpeakerKeeper.cs` | Full source, single file. |
| `SpeakerKeeper.ico` | App icon (also embedded in the exe). |
| `silent.wav` | The silent loop that holds the stream open. |
| `Uninstall.exe` | Removes the app (same as the entry in Settings → Apps). |
| `app.manifest` | `asInvoker` + DPI-aware, for the app. |
| `uninstall.manifest` | `requireAdministrator` + DPI-aware, for the uninstaller. |
| `build.ps1` | Builds both exes. |
| `install.ps1` | Machine-wide installer (run elevated). |

The log is **not** in this folder — it lives in `%LocalAppData%\Speaker Keeper`.

### About `silent.wav`

It is **true digital silence**, not white noise or a faint tone: 20 seconds of 44.1 kHz
16-bit stereo where all 1,764,000 samples are exactly zero.

It isn't there to make a sound — it's the payload the Windows media API needs in order
to have something to "play". The keep-alive comes from the *session*, not the audio:
Windows only holds the Bluetooth A2DP link up while an output stream is open, so the
app keeps a stream open that happens to carry nothing but zeros. No audio is produced,
and the volume slider position is irrelevant.

## Installing

Speaker Keeper is a **machine-wide** install in `C:\Program Files\Speaker Keeper`, like
ordinary Windows software. Build, then run the installer elevated:

```powershell
.\build.ps1
Start-Process powershell -Verb RunAs -ArgumentList '-ExecutionPolicy','Bypass','-File','install.ps1'
```

`install.ps1` copies the runtime payload (exe, uninstaller, icon, wav, README — not the
source or build scripts), writes the uninstall entry to HKLM, creates a Start Menu
shortcut, and enables autostart for the installing user.

### What goes where

| Path | Contents | Writable by |
|---|---|---|
| `C:\Program Files\Speaker Keeper` | exe, uninstaller, icon, `silent.wav` | admin only |
| `%LocalAppData%\Speaker Keeper` | `SpeakerKeeper.log` | the user |
| `HKCU\Software\SpeakerKeeper` | preferences | the user |
| `HKCU\...\CurrentVersion\Run` | autostart entry | the user |
| `HKLM\...\Uninstall\SpeakerKeeper` | Programs and Features entry | admin only |

The split matters: **Program Files is read-only for standard users**, so nothing written
at runtime may live there. The log therefore goes to `%LocalAppData%`, and the app runs
`asInvoker` — it never needs elevation after install.

Autostart is per-user rather than `HKLM\...\Run` on purpose, so the **Start with Windows**
checkbox works without an admin prompt. Each account decides for itself.

## Uninstalling

- Settings → Apps → Installed apps → Speaker Keeper → Uninstall
- `Uninstall.exe` in the install folder

Both run the same program. `Uninstall.exe` is built from this same source with
`/main:UninstallProgram`, so the removal logic can't drift from the app's own idea of what
it installed. Its manifest requests `requireAdministrator`, so it shows the normal UAC
prompt — removing anything from Program Files and HKLM does require elevation.

It stops the keep-alive, removes the autostart entry, preferences, tray-icon visibility
preference, the uninstall entry and the log folder, then deletes the install folder. The
exe can't delete the folder it is running from, so it hands that last step to a detached
`cmd` that waits for the process to exit first.

`Uninstall.exe --quiet` skips the confirmation prompt.

> Preferences and autostart are per-user, so uninstalling only clears them for the account
> that runs it. Other users may keep a stale Run entry pointing at the removed exe, which
> fails silently at sign-in.

## Auto-update

A Program Files install cannot replace its own files without elevation — a Windows
security boundary, not a limitation of this app. The standard answer is a privileged
helper installed once: Chrome and Edge use an updater **service** running as SYSTEM,
others a **scheduled task** with highest privileges. Speaker Keeper uses the task.

**Off by default.** Tick *Install updates automatically* in Settings to opt in. That
raises one UAC prompt, because creating a SYSTEM task is a machine-wide change. Every
update after that is silent.

| | |
|---|---|
| Task | `Speaker Keeper Update`, daily at 03:00, `SYSTEM`, highest privileges |
| Runs | `powershell.exe -File "C:\Program Files\Speaker Keeper\update.ps1"` |
| Flag | `HKLM\Software\SpeakerKeeper\AutoUpdate` |
| Feed | `HKLM\Software\SpeakerKeeper\UpdateUrl` — defaults to the repo manifest (see [RELEASING.md](RELEASING.md)) |
| Log | `%ProgramData%\Speaker Keeper\update.log` |

The feed is hosted on GitHub at no cost: the binaries are release assets and the
manifest is `manifest.json` on `main`, served raw. `install.ps1` defaults `UpdateUrl` to

```
https://raw.githubusercontent.com/kevinabouhanna/speaker-keeper/main/manifest.json
```

which is a permanent URL — only its contents change per release. A GitHub Actions
workflow regenerates it from each release's own assets, so a declared hash can never
drift from the published binary. The full process is in **[RELEASING.md](RELEASING.md)**.

Override it for a self-hosted or private feed, at install time or later:

```powershell
.\install.ps1 -UpdateUrl 'https://example.com/speakerkeeper/manifest.json'
Set-ItemProperty HKLM:\Software\SpeakerKeeper UpdateUrl 'https://...'
```

With no `UpdateUrl` at all the updater logs "no UpdateUrl configured" and exits cleanly.

### Manifest format

```json
{
  "version": "1.1.0",
  "files": [
    { "name": "SpeakerKeeper.exe", "url": "https://.../SpeakerKeeper.exe", "sha256": "ABC..." },
    { "name": "Uninstall.exe",     "url": "https://.../Uninstall.exe",     "sha256": "DEF..." }
  ]
}
```

### How a swap avoids downtime

Windows won't let a running exe be **deleted**, but it will let it be **renamed**. So the
updater moves `SpeakerKeeper.exe` aside to `SpeakerKeeper.exe.old` and drops the new
binary in its place. The running copy keeps executing from the renamed file, and the new
version takes effect at next launch — no killing the user's tray app, no gap where the
speaker could fall asleep.

### Safety rules

Everything is downloaded and verified **before** anything in the install folder is
touched, so a failed update leaves a working install:

- **HTTPS required.** Plain HTTP is refused, except on `127.0.0.1`/`localhost` so the
  update path can be tested without a certificate.
- **SHA-256 must match** the manifest for every file, or the update aborts untouched.
- **File names are validated** — anything containing a path separator is rejected, so a
  manifest can't write outside the install directory.

> These binaries are **not code-signed**. Integrity rests on HTTPS plus the manifest
> hashes, which means whoever controls the update URL controls what runs as SYSTEM on
> every install. Treat that host as production infrastructure. Authenticode signing is
> the next step up if this gets real distribution.

## Autostart

Controlled by the **Start with Windows** item in the tray menu (and mirrored in
Settings). It writes:

```
HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Speaker Keeper
```

The Run key is used rather than a Startup-folder shortcut so the checkbox can toggle it
without any COM interop; it still appears in Task Manager's Startup tab. On launch the
app repoints a stale entry at its own current path, so moving the install folder doesn't
break autostart.

Note that disabling the entry from Task Manager's Startup tab sets a separate approval
flag that the checkbox doesn't read, so the two can disagree — toggle it from the tray
menu to be sure.

## Rebuilding

```powershell
.\build.ps1
```

No SDK or Visual Studio needed. It uses the .NET Framework compiler already on Windows
plus the WinRT metadata in `C:\Windows\System32\WinMetadata`. Note that the Windows SDK's
`UnionMetadata\Windows.winmd` does *not* work here — it forwards the media types to a
`UniversalApiContract` version that doesn't match what's installed, so the per-namespace
winmds in System32 are referenced directly instead.

The app name shown in the Volume Mixer comes from the `AssemblyTitle` attribute at the top
of `SpeakerKeeper.cs` (it becomes the Win32 `FileDescription`). Change it there and rebuild
to rename it.
