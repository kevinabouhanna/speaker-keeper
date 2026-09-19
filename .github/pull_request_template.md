<!--
Thanks for this. A few notes before you post — see CONTRIBUTING.md for the longer
version.
-->

## What does this change?

<!-- And why. If there's an issue, link it: "Fixes #12". -->

## How did you test it?

<!--
The binary is the product; the source is not. Several things here — installing,
updating, the tray panel — only fail once packaged.
-->

- [ ] `.\build.ps1` succeeds
- [ ] `.\verify.ps1` exits 0
- [ ] I ran the built `Install.exe` and used the app

## Checklist

- [ ] Comments explain *why*, matching the surrounding style
- [ ] No new build dependencies — it still builds with what ships with Windows
- [ ] `CHANGELOG.md` has an entry under `## [Unreleased]`, written for the person
      using the app rather than the person reading the diff
- [ ] If this touches installing or updating, I've read [docs/RELEASING.md](../docs/RELEASING.md)
