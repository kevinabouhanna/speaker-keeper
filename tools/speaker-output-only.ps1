# Makes Bluetooth speakers output-only by switching off their Hands-Free profile.
#
# Why: the Hands-Free profile is what gives a speaker a microphone, and an app opening
# that microphone (voice chat, calls) moves the speaker into call mode. On the Xiaomi
# Sound Pockets this was found on, Windows also makes that microphone its Default
# Communication Device every time the speaker reconnects, so changing the setting by
# hand does not stick. With the profile off there is no microphone to hand out.
# See docs/INVESTIGATION-auto-off.md, F2.
#
# Same effect as unticking "Handsfree Telephony" under Devices and Printers > the
# speaker > Properties > Services. Re-pairing a speaker brings the profile back.
#
#   .\speaker-output-only.ps1 "Xiaomi Sound Pocket"            switch it off
#   .\speaker-output-only.ps1 "Xiaomi Sound Pocket" -Restore   switch it back on
#
# Needs an elevated PowerShell; it relaunches itself elevated if it is not.
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [switch]$Restore
)

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", "`"$Name`"")
    if ($Restore) { $argList += '-Restore' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait
    exit
}

$log = Join-Path $env:TEMP 'speaker-output-only.log'
"--- $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $(if ($Restore) { 'restore' } else { 'output-only' }) '$Name'" | Out-File $log -Append

# The Hands-Free AG node, one per paired speaker: {0000111E} is the Hands-Free service.
$nodes = Get-PnpDevice | Where-Object {
    $_.InstanceId -like 'BTHENUM\{0000111E-*' -and $_.FriendlyName -like "$Name*"
}
if (-not $nodes) { "no Hands-Free node found for '$Name'" | Out-File $log -Append; exit 1 }

foreach ($n in $nodes) {
    try {
        if ($Restore) { Enable-PnpDevice -InstanceId $n.InstanceId -Confirm:$false -ErrorAction Stop }
        else { Disable-PnpDevice -InstanceId $n.InstanceId -Confirm:$false -ErrorAction Stop }
        "ok      $($n.InstanceId)" | Out-File $log -Append
    }
    catch { "FAILED  $($n.InstanceId): $_" | Out-File $log -Append }
}
Get-PnpDevice -InstanceId $nodes.InstanceId | ForEach-Object { "now $($_.Status)  $($_.InstanceId)" } | Out-File $log -Append
