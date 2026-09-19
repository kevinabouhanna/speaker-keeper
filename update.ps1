# Speaker Keeper auto-updater.
#
# Run by the "Speaker Keeper Update" scheduled task as SYSTEM, which is what lets it
# write to Program Files without prompting anyone. It is deliberately conservative:
# nothing is installed unless it is newer, served over HTTPS, and matches the SHA-256
# the manifest declares.
#
# Manifest format (JSON):
#   {
#     "version": "1.1.0",
#     "files": [
#       { "name": "SpeakerKeeper.exe", "url": "https://.../SpeakerKeeper.exe", "sha256": "ABC..." },
#       { "name": "Uninstall.exe",     "url": "https://.../Uninstall.exe",     "sha256": "DEF..." }
#     ]
#   }
#
# This script is itself in that list, so an update replaces the copy that is running it.
# PowerShell reads a script in full before executing, and Windows allows an open file to
# be renamed, so the swap below can move this file aside safely. The running process
# carries on from memory; the new script takes over at the next nightly check.
[CmdletBinding()]
param(
    [switch]$Force,          # install even if the version isn't newer (for testing)
    [string]$ManifestUrl     # overrides the registry value (for testing)
)

$ErrorActionPreference = "Stop"

$InstallDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$MachineKey = "HKLM:\Software\SpeakerKeeper"
$LogDir     = Join-Path $env:ProgramData "Speaker Keeper"
$LogFile    = Join-Path $LogDir "update.log"

function Write-Log([string]$msg) {
    try {
        if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Force $LogDir | Out-Null }
        $line = (Get-Date -Format "yyyy-MM-dd HH:mm:ss") + "  " + $msg
        Add-Content -Path $LogFile -Value $line -Encoding utf8
        Write-Output $line
    } catch { }
}

# Only HTTPS is accepted for real hosts. Loopback over plain HTTP is allowed so the
# update path can be tested end to end without a certificate.
function Assert-SafeUrl([string]$url) {
    $u = [Uri]$url
    if ($u.Scheme -eq "https") { return }
    if ($u.Scheme -eq "http" -and ($u.Host -eq "127.0.0.1" -or $u.Host -eq "localhost")) { return }
    throw "refusing non-HTTPS url: $url"
}

# Deletes what older versions left behind in the install folder.
#
# This runs on EVERY invocation, before the version comparison, and that is the whole
# point. The script that runs on a user's machine is whatever copy is already installed,
# so a cleanup that only fired while applying an update would sit dormant until the
# release AFTER the one that delivered it. Running it unconditionally means the first
# nightly check after this script lands does the work.
#
# Scoped to exact names and to *.old inside the install folder, and every failure is
# swallowed: this runs as SYSTEM, and nothing here is worth failing an update over.
function Remove-Stale {
    # silent.wav: 3.4 MB of zeros that versions up to 1.2.1 installed. Nothing has read
    # it since 1.3.0 - the app renders its own silence now.
    $dead = @("silent.wav")
    foreach ($name in $dead) {
        $f = Join-Path $InstallDir $name
        if (-not (Test-Path $f)) { continue }
        Remove-Item $f -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path $f)) { Write-Log ("removed obsolete " + $name) }
    }

    # The previous copy of each file an update replaced. The one the running app is
    # executing from cannot be deleted while it runs, so this quietly leaves it for the
    # next pass rather than treating it as a problem.
    foreach ($f in @(Get-ChildItem -Path $InstallDir -Filter "*.old" -File -ErrorAction SilentlyContinue)) {
        Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path $f.FullName)) { Write-Log ("removed leftover " + $f.Name) }
    }
}

function Get-InstalledVersion {
    $exe = Join-Path $InstallDir "SpeakerKeeper.exe"
    if (-not (Test-Path $exe)) { return [Version]"0.0.0.0" }
    return [Version](Get-Item $exe).VersionInfo.FileVersion
}

try {
    Write-Log "--- update check starting"
    Remove-Stale

    if (-not $ManifestUrl) {
        $ManifestUrl = (Get-ItemProperty $MachineKey -Name UpdateUrl -ErrorAction SilentlyContinue).UpdateUrl
    }
    if (-not $ManifestUrl) {
        Write-Log "no UpdateUrl configured - nothing to do"
        exit 0
    }

    Assert-SafeUrl $ManifestUrl
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $manifest = Invoke-RestMethod -Uri $ManifestUrl -UseBasicParsing -TimeoutSec 60
    $remote = [Version]$manifest.version
    $local  = Get-InstalledVersion
    Write-Log ("installed {0}, available {1}" -f $local, $remote)

    if (-not $Force -and $remote -le $local) {
        Write-Log "already up to date"
        exit 0
    }

    # --- download and verify everything BEFORE touching the install ------------
    $staging = Join-Path ([IO.Path]::GetTempPath()) ("SpeakerKeeperUpdate_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force $staging | Out-Null
    $staged = @()

    foreach ($f in $manifest.files) {
        if (-not $f.name -or -not $f.url -or -not $f.sha256) { throw "manifest entry missing name/url/sha256" }
        # Reject anything that isn't a plain file name, so a manifest can't write outside the install dir.
        if ($f.name -match '[\\/]' -or $f.name -eq ".." ) { throw ("illegal file name in manifest: " + $f.name) }
        Assert-SafeUrl $f.url

        $dest = Join-Path $staging $f.name
        Write-Log ("downloading " + $f.name)
        Invoke-WebRequest -Uri $f.url -OutFile $dest -UseBasicParsing -TimeoutSec 300

        $actual = (Get-FileHash -Path $dest -Algorithm SHA256).Hash
        if ($actual -ne $f.sha256.ToUpperInvariant()) {
            throw ("sha256 mismatch for " + $f.name + ": expected " + $f.sha256 + ", got " + $actual)
        }
        Write-Log ("  verified " + $f.name + "  " + $actual.Substring(0, 16) + "...")
        $staged += [pscustomobject]@{ Name = $f.name; Path = $dest }
    }

    # --- swap the files ----------------------------------------------------------
    # Windows won't let a running exe be deleted, but it WILL let it be renamed. So the
    # old binary is moved aside and the new one dropped in its place; the running copy
    # keeps executing from the renamed file and the update takes effect on next launch.
    # This means no downtime and no need to kill the user's tray app.
    foreach ($s in $staged) {
        $target = Join-Path $InstallDir $s.Name
        if (Test-Path $target) {
            $old = "$target.old"
            if (Test-Path $old) { Remove-Item $old -Force -ErrorAction SilentlyContinue }
            Move-Item $target $old -Force
        }
        Move-Item $s.Path $target -Force
        Write-Log ("  installed " + $s.Name)
    }

    Set-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SpeakerKeeper" `
        -Name "DisplayVersion" -Value $manifest.version -ErrorAction SilentlyContinue

    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    Write-Log ("updated to {0} - takes effect next time the app starts" -f $remote)
    exit 0
}
catch {
    Write-Log ("FAILED: " + $_.Exception.Message)
    exit 1
}
