# Machine-wide installer for Speaker Keeper. Must run elevated.
#
#   powershell -ExecutionPolicy Bypass -File install.ps1
#
# Installs to Program Files, registers the app in the machine-wide uninstall list,
# and sets up per-user autostart for the invoking user.
[CmdletBinding()]
param(
    [string]$Source = $PSScriptRoot,
    [string]$Target = (Join-Path $env:ProgramFiles "Speaker Keeper"),
    # HTTPS URL of the release manifest the auto-updater polls. Defaults to the
    # manifest in the public repo, which a GitHub Actions workflow regenerates from
    # each release's own assets. Override for a private/self-hosted feed, or set later:
    #   Set-ItemProperty HKLM:\Software\SpeakerKeeper UpdateUrl 'https://...'
    [string]$UpdateUrl = "https://raw.githubusercontent.com/kevinabouhanna/speaker-keeper/main/manifest.json"
)

$ErrorActionPreference = "Stop"

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "install.ps1 must be run as Administrator (Program Files and HKLM need elevation)."
}

Write-Output "Installing Speaker Keeper to $Target"

# --- stop anything already running ------------------------------------------
Get-Process -Name "SpeakerKeeper" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Output ("  stopping pid " + $_.Id)
    Stop-Process -Id $_.Id -Force
}
Start-Sleep -Seconds 1

# --- lay down the payload ----------------------------------------------------
if (-not (Test-Path $Target)) { New-Item -ItemType Directory -Force $Target | Out-Null }

# Only what the app needs at runtime. Source and build scripts stay out of Program Files.
$payload = @("SpeakerKeeper.exe", "Uninstall.exe", "SpeakerKeeper.ico", "silent.wav",
             "README.md", "update.ps1")
foreach ($f in $payload) {
    $src = Join-Path $Source $f
    if (-not (Test-Path $src)) { throw "missing payload file: $src" }
    Copy-Item $src (Join-Path $Target $f) -Force
    Write-Output "  + $f"
}

# --- machine-wide uninstall entry -------------------------------------------
$key = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SpeakerKeeper"
if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }

$size = 0
Get-ChildItem $Target -File | ForEach-Object { $size = $size + $_.Length }

$exe = Join-Path $Target "SpeakerKeeper.exe"
$uninst = Join-Path $Target "Uninstall.exe"

Set-ItemProperty $key -Name "DisplayName"          -Value "Speaker Keeper"
Set-ItemProperty $key -Name "DisplayVersion"       -Value "1.0.0"
Set-ItemProperty $key -Name "Publisher"            -Value "Kevin Abou Hanna"
Set-ItemProperty $key -Name "DisplayIcon"          -Value ($exe + ",0")
Set-ItemProperty $key -Name "InstallLocation"      -Value $Target
Set-ItemProperty $key -Name "UninstallString"      -Value ('"' + $uninst + '"')
Set-ItemProperty $key -Name "QuietUninstallString" -Value ('"' + $uninst + '" --quiet')
Set-ItemProperty $key -Name "EstimatedSize" -Value ([int]($size / 1024)) -Type DWord
Set-ItemProperty $key -Name "NoModify" -Value 1 -Type DWord
Set-ItemProperty $key -Name "NoRepair" -Value 1 -Type DWord
Write-Output "  registered in Programs and Features"

# --- update channel -----------------------------------------------------------
# The auto-update scheduled task is NOT created here: it is off by default and the
# user opts in from Settings, which raises its own elevation prompt.
$machine = "HKLM:\Software\SpeakerKeeper"
if (-not (Test-Path $machine)) { New-Item -Path $machine -Force | Out-Null }
if ($UpdateUrl) {
    Set-ItemProperty $machine -Name "UpdateUrl" -Value $UpdateUrl
    Write-Output "  update url set to $UpdateUrl"
} else {
    $existing = (Get-ItemProperty $machine -Name UpdateUrl -ErrorAction SilentlyContinue).UpdateUrl
    if (-not $existing) { Write-Output "  no update url configured (auto-update will no-op)" }
}

# --- Start Menu shortcut ------------------------------------------------------
$sm = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\Speaker Keeper.lnk"
$sh = New-Object -ComObject WScript.Shell
$lnk = $sh.CreateShortcut($sm)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $Target
$lnk.IconLocation = "$exe,0"
$lnk.Description = "Keeps a Bluetooth speaker awake"
$lnk.Save()
Write-Output "  Start Menu shortcut created"

# --- autostart for the user who is actually installing -----------------------
# Per-user on purpose: each account decides for itself, and toggling it from the
# tray menu then needs no elevation.
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $runKey -Name "Speaker Keeper" -Value ('"' + $exe + '"')
Write-Output "  autostart enabled for $env:USERNAME"

# --- clean up the old per-user install ---------------------------------------
$old = Join-Path $env:LOCALAPPDATA "SpeakerKeeper"
if ((Test-Path $old) -and ($old -ne $Target)) {
    Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue
    Write-Output "  removed old per-user install at $old"
}
Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SpeakerKeeper" `
    -Recurse -Force -ErrorAction SilentlyContinue

# Deliberately does NOT launch the app here: this script is elevated, and anything
# it starts would inherit that. The tray app is meant to run as a normal user.
Write-Output "Done. Start it from the Start Menu, or it will run at your next sign-in."
