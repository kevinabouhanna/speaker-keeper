# Checks that a build is coherent enough to release.
#
# Publishing is the one irreversible act in this project: a release is pushed to every
# installed copy by a SYSTEM task, overnight, with no prompt, and it cannot be recalled -
# see docs/RELEASING.md. Everything here is something that has either already gone wrong
# or would fail silently on other people's machines rather than on this one.
#
#   .\build.ps1
#   .\verify.ps1
#
# Exits non-zero on the first category of failure, and prints every problem it found.
[CmdletBinding()]
param(
    # Folder holding the built exes. Defaults to the repo root, where build.ps1 puts them.
    [string]$BuildDir = $PSScriptRoot,
    # Expected version, e.g. "1.2.0". Defaults to whatever SpeakerKeeper.exe declares.
    [string]$Version
)

$ErrorActionPreference = "Stop"

# A crash in the checker must not read as a pass. Without this, an exception here
# propagates out and the caller still sees exit 0.
trap { Write-Host "`nverify.ps1 itself failed: $_" -ForegroundColor Red; exit 1 }

$repo = $PSScriptRoot
$fail = @()
$warn = @()

function Ok   ([string]$m) { Write-Host "  [ok]   $m" -ForegroundColor Green }
function Bad  ([string]$m) { $script:fail += $m; Write-Host "  [FAIL] $m" -ForegroundColor Red }
function Warn ([string]$m) { $script:warn += $m; Write-Host "  [warn] $m" -ForegroundColor Yellow }

Write-Host "Verifying build in $BuildDir" -ForegroundColor Cyan

# --- 1. the three exes exist ---------------------------------------------------
Write-Host "`nBinaries"
$exes = @("SpeakerKeeper.exe", "Uninstall.exe", "Install.exe")
$missing = @()
foreach ($e in $exes) {
    $p = Join-Path $BuildDir $e
    if (Test-Path $p) { Ok "$e present ($('{0:N0}' -f (Get-Item $p).Length) bytes)" }
    else { Bad "$e is missing - run build.ps1 first"; $missing += $e }
}
if ($missing.Count -gt 0) {
    Write-Host "`nCannot continue without all three binaries." -ForegroundColor Red
    exit 1
}

# --- 2. versions agree ---------------------------------------------------------
# A manifest version that doesn't match the version stamped in the binary makes every
# client reinstall the same build every night forever, because update.ps1 compares the
# manifest against the installed exe's FileVersion and would never see it advance.
Write-Host "`nVersion consistency"
$versions = @{}
foreach ($e in $exes) {
    $versions[$e] = (Get-Item (Join-Path $BuildDir $e)).VersionInfo.FileVersion
}
# @() forces an array: a single unique value comes back as a bare string, and indexing
# a string yields its first character rather than the version.
$distinct = @($versions.Values | Sort-Object -Unique)
if ($distinct.Count -eq 1) { Ok "all three exes report $($distinct[0])" }
else {
    Bad "exes disagree on version: $(($versions.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ')"
}

if (-not $Version) {
    $fv = [Version]$versions["SpeakerKeeper.exe"]
    $Version = "{0}.{1}.{2}" -f $fv.Major, $fv.Minor, $fv.Build
    Write-Host "  (no -Version given; using $Version from the binary)"
}

$expected = [Version]"$Version.0"
foreach ($e in $exes) {
    if ([Version]$versions[$e] -ne $expected) {
        Bad "$e is $($versions[$e]) but the release is $Version - rebuild after bumping AssemblyVersion"
    }
}
if ($fail.Count -eq 0) { Ok "binaries match the intended release version $Version" }

# --- 3. manifests carry the same version ---------------------------------------
foreach ($m in @("app.manifest", "install.manifest", "uninstall.manifest")) {
    $p = Join-Path $repo $m
    if (-not (Test-Path $p)) { Warn "$m not found"; continue }
    $xml = [xml](Get-Content $p -Raw)
    $v = $xml.assembly.assemblyIdentity.version
    if ($v -eq "$Version.0") { Ok "$m declares $v" }
    else { Bad "$m declares $v, expected $Version.0" }
}

# --- 4. Install.exe really carries these binaries -------------------------------
# Install.exe embeds copies of the other two. If it was built before the last change to
# them, it would quietly install stale binaries while the release assets say otherwise.
Write-Host "`nInstaller payload"
$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $BuildDir "Install.exe"))
$resources = $asm.GetManifestResourceNames()

$expectedResources = @("SpeakerKeeper.exe", "Uninstall.exe", "SpeakerKeeper.ico",
                       "update.ps1", "README.md", "LICENSE", "Logo.png")
foreach ($r in $expectedResources) {
    if ($resources -contains $r) { Ok "embeds $r" }
    else { Bad "Install.exe is missing embedded resource '$r'" }
}

$sha = [System.Security.Cryptography.SHA256]::Create()
foreach ($e in @("SpeakerKeeper.exe", "Uninstall.exe")) {
    if (-not ($resources -contains $e)) { continue }
    $s = $asm.GetManifestResourceStream($e)
    $ms = New-Object System.IO.MemoryStream
    $s.CopyTo($ms)
    $embedded = [BitConverter]::ToString($sha.ComputeHash($ms.ToArray())).Replace("-", "")
    $s.Dispose(); $ms.Dispose()
    $onDisk = (Get-FileHash (Join-Path $BuildDir $e) -Algorithm SHA256).Hash
    if ($embedded -eq $onDisk) { Ok "embedded $e matches the one beside it" }
    else { Bad "embedded $e is STALE - Install.exe was built from an older $e; rebuild" }
}

# --- 5. silent.wav generation still matches the repo copy -----------------------
# The installer generates this rather than shipping it. If generation ever drifts, the
# app would loop a malformed file and hold no session at all.
Write-Host "`nGenerated payload"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("skverify_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    $payloadType = $asm.GetType("Payload")
    $m = $payloadType.GetMethod("WriteSilentWav",
            [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Static)
    # [object[]] with an explicit [string]: reflection rejects a PSObject-wrapped arg.
    $m.Invoke($null, [object[]]@([string]$tmp)) | Out-Null
    $gen = (Get-FileHash (Join-Path $tmp "silent.wav") -Algorithm SHA256).Hash
    $ref = Join-Path $repo "silent.wav"
    if (Test-Path $ref) {
        $orig = (Get-FileHash $ref -Algorithm SHA256).Hash
        if ($gen -eq $orig) { Ok "generated silent.wav is byte-identical to the repo copy" }
        else { Bad "generated silent.wav differs from silent.wav in the repo" }
    } else { Warn "silent.wav not in repo; cannot compare (generated $gen)" }
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

# --- 6. the changelog documents this version ------------------------------------
# Updates are silent, so an undocumented release is one nobody can find out about.
Write-Host "`nChangelog"
$clPath = Join-Path $repo "CHANGELOG.md"
if (-not (Test-Path $clPath)) { Bad "CHANGELOG.md is missing" }
else {
    $cl = Get-Content $clPath -Raw
    if ($cl -match "(?m)^##\s*\[$([regex]::Escape($Version))\]") { Ok "CHANGELOG.md has an entry for $Version" }
    else { Bad "CHANGELOG.md has no '## [$Version]' heading - write the release notes before releasing" }

    if ($cl -match "(?ms)^##\s*\[Unreleased\]\s*(.*?)(?=^##\s)") {
        $body = $Matches[1].Trim()
        if ($body -and $body -notmatch '^(Nothing yet\.?|_Nothing yet\._)$') {
            Warn "Unreleased section still has content - did you mean to include it in $Version?"
        } else { Ok "Unreleased section is empty" }
    }
}

# --- 7. nothing uncommitted -----------------------------------------------------
Write-Host "`nWorking tree"
try {
    Push-Location $repo
    $dirty = git status --porcelain 2>$null
    if ($LASTEXITCODE -eq 0) {
        if ([string]::IsNullOrWhiteSpace($dirty)) { Ok "working tree is clean" }
        else { Warn "uncommitted changes - the release would not match main:`n$dirty" }
    }
} catch { } finally { Pop-Location }

# --- summary --------------------------------------------------------------------
Write-Host ""
if ($fail.Count -gt 0) {
    Write-Host "FAILED - $($fail.Count) problem(s), do not release:" -ForegroundColor Red
    $fail | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
if ($warn.Count -gt 0) {
    Write-Host "Passed with $($warn.Count) warning(s):" -ForegroundColor Yellow
    $warn | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
} else {
    Write-Host "All checks passed. Safe to release $Version." -ForegroundColor Green
}
exit 0
