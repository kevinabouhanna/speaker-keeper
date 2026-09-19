# Releasing

Updates are hosted entirely on GitHub — no server, no domain, no cost.

| Piece | Where it lives |
|---|---|
| Binaries | Release assets: `https://github.com/kevinabouhanna/speaker-keeper/releases/download/vX.Y.Z/SpeakerKeeper.exe` |
| Manifest | `manifest.json` on `main`, served raw |
| Feed URL | `https://raw.githubusercontent.com/kevinabouhanna/speaker-keeper/main/manifest.json` |

That feed URL is baked into `Install.exe` and written to
`HKLM\Software\SpeakerKeeper\UpdateUrl` at install time. **It never changes** — only the
contents of `manifest.json` change when you ship. Installed copies check it daily at
03:00 via the scheduled task, for users who ticked *Install updates automatically*.

## What a release actually is

A release is not a checkpoint. It is **a silent, unattended, irreversible push of new
binaries into `C:\Program Files` on every machine that opted in**, performed by a task
running as `SYSTEM` at 03:00, with no prompt and no confirmation.

Read [When to release](#when-to-release) before cutting one, and understand
[Rollback](#rollback) before you need it.

## When to release

Never by commit count. Release when a *user-visible* change is finished and tested;
let everything else accumulate on `main`.

| Release | Don't release |
|---|---|
| A bug users can hit is fixed | Refactors, renames, comment cleanups |
| A feature is finished and tested as a built binary | Docs-only changes |
| A security fix (immediately) | Work in progress behind an unfinished UI |
| The install or update path changed | Anything not yet run as a real `Install.exe` |

Versions follow [semver](https://semver.org): **PATCH** for a fix, **MINOR** for a new
capability, **MAJOR** for breaking where settings or install paths live.

## Shipping a version

1. **Move `Unreleased` into a new version heading** in `CHANGELOG.md`:

   ```markdown
   ## [1.2.0] - 2026-10-04
   ```

   and add the compare/tag links at the bottom of the file.

2. **Bump the version in four places** — `SpeakerKeeper.cs` (`AssemblyVersion` *and*
   `AssemblyFileVersion`), `app.manifest`, `install.manifest`, `uninstall.manifest`.

3. **Build and verify.** `verify.ps1` exits non-zero if anything is inconsistent:

   ```powershell
   .\build.ps1
   .\verify.ps1
   ```

4. **Install it on a real machine and run it.** The binary is the product; the source is
   not. This is the only step that catches a packaging mistake.

5. **Commit and push**, then **create the release** — tag `v1.2.0`, attaching all four:

   | Asset | Who downloads it |
   |---|---|
   | `Install.exe` | People. This is the README's download link. |
   | `SpeakerKeeper.exe` | The auto-updater, which swaps it in place. |
   | `Uninstall.exe` | The auto-updater, same. |
   | `update.ps1` | The auto-updater, replacing itself. |

   Skipping any of the last three means every auto-update 404s, because the manifest
   points at them by name. `Install.exe` embeds its own copies, so it is self-contained.

   `update.ps1` is in the manifest so the updater itself can be fixed on machines that
   only ever update in the background — otherwise the only copy that ever runs there
   is whatever shipped with the last `Install.exe` the user ran by hand. It is listed
   **last**, so it replaces itself only after the binaries have landed.

6. **Nothing else.** Publishing fires
   [`release-manifest.yml`](../.github/workflows/release-manifest.yml), which verifies
   the release, regenerates `manifest.json` from the published assets, and commits it.

> **Attach the assets before publishing**, or create the release as a draft and publish
> once they are up. GitHub fires `published` the instant a release is created, usually
> before uploads finish. The workflow waits up to 2.5 minutes per asset; a slow
> connection can still outlast that, in which case just re-run the workflow.

> Don't hand-edit `manifest.json`. A hash that doesn't match the published binary makes
> every client abort its update — silently, at 03:00, on other people's machines. The
> workflow hashes the real assets so that cannot happen. To rebuild a manifest for an
> existing tag: **Actions → Update release manifest → Run workflow**.

## The mistake that breaks every install at once

**The version stamped in the binaries must equal the tag.**

`update.ps1` decides whether to update by comparing the manifest version against the
**installed exe's `FileVersion`**. Publish a manifest that says `1.2.0` while the binary
is still stamped `1.1.0`, and:

1. Client sees `1.2.0 > 1.1.0`, downloads, installs.
2. The installed exe still reports `1.1.0`, because that is what is compiled into it.
3. Next night: `1.2.0 > 1.1.0` again. Installs again.
4. Forever, on every machine, with nothing shown to the user.

Both `verify.ps1` and the release workflow refuse to proceed when the tag and the
binaries disagree. That check exists solely to prevent this.

## Rollback

**There is no rollback. You can only go forwards.**

`update.ps1` installs only when the manifest version is *strictly newer* than what is
installed. Re-publishing `1.1.0` after a bad `1.2.0` does nothing: every client already
has `1.2.0` and skips it. Those users are stuck on the broken build.

### The wrong fix

Re-uploading the old `1.1.0` binaries under a new `1.2.1` tag looks like the obvious
move. It creates the reinstall loop above: the manifest would say `1.2.1` while those
binaries are stamped `1.1.0`, so every client reinstalls them every night forever.
`verify.ps1` and the workflow both reject this.

### The right fix

Ship the old *code* as a genuinely new version, so the binaries carry a version number
that is actually higher:

```powershell
# 1. Revert the bad change on main (keeping history honest)
git revert <bad-commit>            # or: git revert <first>..<last>

# 2. Bump to a NEW patch version - 1.2.1, never back to 1.1.0
#    SpeakerKeeper.cs + the three .manifest files

# 3. Record it
#    CHANGELOG.md: ## [1.2.1] - <date>, under "Fixed"

# 4. Build, verify, and TEST the binary
.\build.ps1
.\verify.ps1
.\Install.exe

# 5. Release v1.2.1 with all four assets
```

Clients move `1.2.0 → 1.2.1` and land on the reverted code. Because the binaries are
stamped `1.2.1`, they settle there and stop.

### If the bad build can't even start

The updater is a separate SYSTEM scheduled task, not part of the tray app, so it still
repairs the install on its next run even if the app itself never launches. But anyone
who turned auto-update *off* is stranded, and their only route back is downloading
`Install.exe` by hand. That is the real cost of a bad release, and the reason step 4
above is not optional.

### Urgency

The task runs daily at 03:00, so a fix published today reaches most machines within 24
hours. Nothing makes it faster — there is no push channel. Plan around that.

## Verifying an update actually works

`raw.githubusercontent.com` caches for about five minutes, so a just-pushed manifest
isn't instantly visible. Once it is:

```powershell
# Force an update check now instead of waiting for 03:00
Start-Process powershell -Verb RunAs -ArgumentList '-ExecutionPolicy','Bypass','-File',`
  '"C:\Program Files\Speaker Keeper\update.ps1"','-Force'

Get-Content "$env:ProgramData\Speaker Keeper\update.log" -Tail 20
```

`update.ps1 -ManifestUrl` points at a different feed for testing, and plain HTTP is
accepted on `127.0.0.1`/`localhost` only, so the whole path can be exercised against a
local server without a certificate.

## Telling users what changed

Because updates are silent, the app has to volunteer this:

- **Settings shows the running version** plus a **What's new** link to that version's
  release notes. This is the reliable channel — it cannot be suppressed.
- **A notification appears once** on the first launch after the version changes, and
  opens the release notes when clicked. Best-effort: if notifications are off
  system-wide (`ToastEnabled = 0`), the shell drops it silently.
- **`CHANGELOG.md`** is the written record, and both the workflow and `verify.ps1`
  refuse to release a version that isn't in it.

Version tracking is per-user (`HKCU\Software\SpeakerKeeper\LastRunVersion`), so every
account that signs in gets told once, rather than only whoever logged in first. A first
run and a downgrade are deliberately not reported as updates.

## Security

Whoever controls this repo controls what runs as **SYSTEM** on every install that has
auto-update enabled. The binaries are not Authenticode-signed, so HTTPS plus the
manifest's SHA-256 are the only integrity anchors.

- Keep 2FA on the GitHub account, and treat the repo as production infrastructure.
- Code signing (~$200+/yr for an OV certificate) is the real upgrade path, and also
  removes the SmartScreen warning new users hit on first run.
