# Builds SpeakerKeeper.exe, Uninstall.exe and Install.exe.
# Plain .NET Framework csc + the Windows metadata in System32, so no SDK is needed.
#
# All three exes come from the same source, selected by /main, so the install and
# uninstall logic can't drift from the app's own idea of what it installed.
#
# Install.exe is built last because it embeds the other two: it carries the whole
# runtime payload as resources, so what users download is a single double-clickable
# file rather than a folder and a script.
$ErrorActionPreference = "Stop"
$dir = $PSScriptRoot
$out = if ($args.Count -gt 0) { $args[0] } else { $dir }

$csc    = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$facade = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll"

# The SDK's union metadata forwards the media types to a UniversalApiContract version
# that isn't installed here; the per-namespace winmds in System32 are self-contained.
$winmds = Get-ChildItem "C:\Windows\System32\WinMetadata" -Filter "*.winmd" |
          Select-Object -ExpandProperty FullName
# The winmds express projected types (IEnumerable, Attribute, ...) via System.Runtime.
$sysRuntime = (Get-ChildItem "C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Runtime" `
                 -Recurse -Filter "System.Runtime.dll" | Select-Object -First 1).FullName
$refs = @("/r:$facade", "/r:$sysRuntime") + ($winmds | ForEach-Object { "/r:$_" })
$fx = @("/r:System.dll", "/r:System.Windows.Forms.dll", "/r:System.Core.dll", "/r:System.Drawing.dll")

if (-not (Test-Path $out)) { New-Item -ItemType Directory -Force $out | Out-Null }

& $csc /nologo /target:winexe /platform:anycpu /main:Program `
    /out:"$out\SpeakerKeeper.exe" `
    /win32icon:"$dir\SpeakerKeeper.ico" `
    /win32manifest:"$dir\app.manifest" `
    @fx @refs "$dir\SpeakerKeeper.cs"
if ($LASTEXITCODE -ne 0) { throw "SpeakerKeeper.exe failed to build" }

& $csc /nologo /target:winexe /platform:anycpu /main:UninstallProgram /define:UNINSTALLER `
    /out:"$out\Uninstall.exe" `
    /win32icon:"$dir\SpeakerKeeper.ico" `
    /win32manifest:"$dir\uninstall.manifest" `
    @fx @refs "$dir\SpeakerKeeper.cs"
if ($LASTEXITCODE -ne 0) { throw "Uninstall.exe failed to build" }

# --- Install.exe ---------------------------------------------------------------
# silent.wav is deliberately NOT embedded: it is 3.4 MB of pure zeros, so the
# installer generates it instead of carrying it. See Payload.WriteSilentWav.
$payload = @(
    "/resource:$out\SpeakerKeeper.exe,SpeakerKeeper.exe",
    "/resource:$out\Uninstall.exe,Uninstall.exe",
    "/resource:$dir\SpeakerKeeper.ico,SpeakerKeeper.ico",
    "/resource:$dir\update.ps1,update.ps1",
    "/resource:$dir\README.md,README.md",
    "/resource:$dir\LICENSE,LICENSE",
    "/resource:$dir\assets\logo-256.png,Logo.png"
)
foreach ($r in $payload) {
    $f = (($r -split ":", 2)[1] -split ",")[0]
    if (-not (Test-Path $f)) { throw "missing payload file for Install.exe: $f" }
}

& $csc /nologo /target:winexe /platform:anycpu /main:SetupProgram /define:INSTALLER `
    /out:"$out\Install.exe" `
    /win32icon:"$dir\SpeakerKeeper.ico" `
    /win32manifest:"$dir\install.manifest" `
    @fx @refs @payload "$dir\SpeakerKeeper.cs" "$dir\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw "Install.exe failed to build" }

foreach ($n in @("SpeakerKeeper.exe", "Uninstall.exe", "Install.exe")) {
    $f = Get-Item (Join-Path $out $n)
    Write-Output ("built {0,-20} {1,7:N0} bytes  [{2}]" -f $f.Name, $f.Length, $f.VersionInfo.FileDescription)
}
