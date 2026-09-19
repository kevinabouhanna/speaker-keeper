# Contributing

Thanks for looking. This is a small project with a deliberately small scope, so the
most useful thing you can do is usually a good bug report rather than a pull request.

Everyone taking part is expected to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Reporting a bug

[Open an issue](https://github.com/kevinabouhanna/speaker-keeper/issues/new/choose) —
the form asks for what's needed. You don't have to be technical.

The **log** is the single most useful thing you can attach: click the tray icon, then
the cog, then **About** → **View log**. It records what the app was doing at the time.
It holds device names and timestamps only — nothing about what you listened to.

For anything security-related, please read [SECURITY.md](SECURITY.md) first and report
it privately instead.

## Suggesting a feature

Describe the situation you're in rather than the feature you have in mind. Speaker
Keeper does one thing on purpose, and a lot of requests turn out to have a simpler
answer than the one they arrived with.

## Building it

You don't need Visual Studio or the .NET SDK. Windows already ships everything:

```powershell
git clone https://github.com/kevinabouhanna/speaker-keeper.git
cd speaker-keeper
.\build.ps1
.\verify.ps1
```

`build.ps1` produces all three executables. `verify.ps1` checks they agree with each
other and with the changelog, and exits non-zero if they don't.

## Pull requests

Open an issue first if it's more than a small fix — it's a shame to write code that
turns out not to fit the project's scope.

If you do send one:

- **Match the surrounding style.** Comments here explain *why*, not *what*. If a line
  looks odd, the comment says what goes wrong without it.
- **No new dependencies.** The whole thing builds with the C# compiler that ships with
  Windows, and stays that way on purpose. No NuGet, no SDK, no runtime to install.
- **Add a changelog entry** under `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md),
  written for the person using the app rather than the person reading the diff.
- **Run `.\verify.ps1`** and make sure it passes.
- **Test the built binary**, not just the source. Several things here — installing,
  updating, the tray — only fail once packaged.

## What happens to releases

Releases go out as silent background updates to everyone who opted in, which makes them
irreversible in practice. [docs/RELEASING.md](docs/RELEASING.md) explains the process
and the mistakes that break every installation at once. Worth a read before proposing
anything that touches installing or updating.
