<div align="center">

<img src="assets/logo-256.png" alt="Speaker Keeper" width="120">

# Speaker Keeper

**Your Bluetooth speaker keeps falling asleep. This fixes that.**

[![Download](https://img.shields.io/github/v/release/kevinabouhanna/speaker-keeper?label=download&style=flat-square)](https://github.com/kevinabouhanna/speaker-keeper/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-blue?style=flat-square)](#requirements)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)

**[speakerkeeper.github.io](https://speakerkeeper.github.io)**

[Download](#install) · [Why you need it](#the-problem) · [FAQ](https://speakerkeeper.github.io/#faq)

</div>

---

## The problem

You pause your music for a minute. Your Bluetooth speaker decides you're done and
powers itself off.

Now every time you press play, you get this:

- The first second or two of audio is **cut off**, because the speaker is still waking up.
- Or nothing plays at all, and you have to **reconnect it by hand**.
- Or it drops off your sound settings entirely until you press its power button again.

It isn't broken. Almost every Bluetooth speaker does this on purpose to save battery —
if there's no sound coming in, it shuts down after a few minutes.

That's great for a speaker in a bag. It's annoying for the speaker sitting on your desk
that you want to behave like it's simply always on.

## What Speaker Keeper does

Speaker Keeper holds a silent audio stream open on your speaker, so it never sees the
gap in playback that makes it shut down.

You hear nothing. Your speaker just stays awake.

It sits in your system tray and stays out of the way:

|                             |                                                                                                      |
| --------------------------- | ---------------------------------------------------------------------------------------------------- |
| 🔋 **Shows your battery**   | One click on the tray icon shows your speaker's charge, and it warns you when it runs low.           |
| 🎧 **Leaves earbuds alone** | Only works on Bluetooth _speakers_. It won't hold your earbuds awake and drain them.                 |
| 🔀 **Follows your speaker** | Switch audio output and it follows along automatically.                                              |
| 🪶 **Tiny**                 | A single small app that follows your Windows theme. No account, no background service, no telemetry. |

## Install

1. **[Download `Install.exe`](https://github.com/kevinabouhanna/speaker-keeper/releases/latest)**
2. **Double-click it** and say yes to the Windows permission prompt.
3. Follow the setup wizard.

That's it. Speaker Keeper appears in your system tray and starts with Windows.

To remove it later: Settings → Apps → Installed apps → **Speaker Keeper** → Uninstall,
just like any other program.

> [!NOTE]
> **Windows will show a blue "Windows protected your PC" warning.** That's SmartScreen —
> it appears for any app that hasn't paid for a code-signing certificate, which this one
> hasn't. Click **More info** → **Run anyway**. If you'd rather not trust a stranger's
> binary, you can [build it yourself](#building-from-source) in about ten seconds.

### Using it

Find the Speaker Keeper icon in your system tray (you may need to click the `^` arrow to
show hidden icons — drag it out to pin it).

- **Click it once** and a panel opens above the tray, the same way the volume and
  brightness panels do. It shows your speaker, whether it's being kept awake and its
  battery, with a switch to turn Speaker Keeper off for that speaker. Click anywhere else
  to close it.
- **Hover** to see your speaker and its battery level without opening anything.
- **The cog** on the panel opens Settings: the low-battery warning, which speakers to keep
  awake, automatic updates and the activity log.
- **The power button** next to it quits, letting your speaker go back to sleeping normally.
  It's on the right-click menu too.

## Requirements

- Windows 10 or Windows 11
- A Bluetooth speaker

No .NET install, no runtime and no dependencies to chase — it uses what ships with Windows.

## Building from source

You don't need Visual Studio or the .NET SDK — Windows already has everything:

```powershell
git clone https://github.com/kevinabouhanna/speaker-keeper.git
cd speaker-keeper
.\build.ps1
.\Install.exe
```

`build.ps1` produces `SpeakerKeeper.exe`, `Uninstall.exe` and `Install.exe`. The installer
carries the whole payload inside itself, so that one file is the entire distributable.

## Under the hood

Curious how it actually works, or want to contribute? The full engineering write-up —
the silent audio stream, battery detection over Bluetooth, the auto-updater and every
design decision — is in **[docs/TECHNICAL.md](docs/TECHNICAL.md)**.

Publishing a new version is documented in **[docs/RELEASING.md](docs/RELEASING.md)**.

## Contributing

Issues and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md), and the
[Code of Conduct](CODE_OF_CONDUCT.md) that goes with them.

If you're reporting a bug, the log at `%LocalAppData%\Speaker Keeper\SpeakerKeeper.log`
says what the app was doing at the time and is the single most useful thing you can
attach. The **About** page in Settings has a button for it.

Found a security problem? Please read [SECURITY.md](SECURITY.md) and report it privately
rather than opening an issue. It matters more than it looks here: with automatic updates
switched on, this repository decides what runs as `SYSTEM` on other people's machines.

## License

[MIT](LICENSE) © Kevin Abou Hanna
