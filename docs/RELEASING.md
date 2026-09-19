# Releasing

Updates are hosted entirely on GitHub — no server, no domain, no cost.

| Piece | Where it lives |
|---|---|
| Binaries | Release assets: `https://github.com/kevinabouhanna/speaker-keeper/releases/download/vX.Y.Z/SpeakerKeeper.exe` |
| Manifest | `manifest.json` on `main`, served raw |
| Feed URL | `https://raw.githubusercontent.com/kevinabouhanna/speaker-keeper/main/manifest.json` |

That feed URL is baked into `install.ps1` as the default `UpdateUrl` and written to
`HKLM\Software\SpeakerKeeper\UpdateUrl` at install time. **It never changes** — only the
contents of `manifest.json` change when you ship. Installed copies check it daily at
03:00 via the scheduled task, for users who ticked *Install updates automatically*.

## Shipping a version

1. **Bump the version** in `SpeakerKeeper.cs`:

   ```csharp
   [assembly: AssemblyVersion("1.1.0.0")]
   [assembly: AssemblyFileVersion("1.1.0.0")]
   ```

2. **Build and commit:**

   ```powershell
   .\build.ps1
   git commit -am "Bump to 1.1.0"; git push
   ```

3. **Create the release** — tag `v1.1.0`, and attach **all three** assets:
   `SpeakerKeeper.exe`, `Uninstall.exe`, and a `SpeakerKeeper-1.1.0-win.zip` for humans.

   The two bare exes are what the updater swaps in place; the zip is what the README's
   download link points at. Skipping the bare exes means the updater 404s.

4. **Nothing else.** Publishing the release fires
   [`.github/workflows/release-manifest.yml`](../.github/workflows/release-manifest.yml),
   which downloads those assets, hashes them, rewrites `manifest.json` and commits it
   to `main`.

> Don't hand-edit `manifest.json`. A hash that doesn't match the published binary makes
> every client abort its update — and it fails silently, in a 03:00 scheduled task, on
> other people's machines. The workflow hashes the real assets so that can't happen.
> To rebuild a manifest for an existing tag, run the workflow manually via
> **Actions → Update release manifest → Run workflow**.

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

## Version comparison

`update.ps1` compares `[Version]$manifest.version` against the installed exe's
`FileVersion` and installs only when the manifest is strictly newer, so re-running a
check is harmless and a rollback needs `-Force`.

## Security

Whoever controls this repo controls what runs as **SYSTEM** on every install that has
auto-update enabled. The binaries are not Authenticode-signed, so HTTPS plus the
manifest's SHA-256 are the only integrity anchors.

- Keep 2FA on the GitHub account, and treat the repo as production infrastructure.
- Code signing (~$200+/yr for an OV certificate) is the real upgrade path, and also
  removes the SmartScreen warning new users hit on first run.
