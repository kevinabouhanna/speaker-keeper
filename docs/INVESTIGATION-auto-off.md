# Investigation: speakers still switching themselves off

**Read this before debugging a speaker that "still turns off".** This same problem has
been debugged from scratch more than once, and each round re-derived the same facts
from the log. Everything established so far is here, with the evidence and how sure we
are. Add to it rather than starting again, and keep the status column honest.

Started 2026-09-22, rewritten 2026-10-01.

## The setup this was found on

| | |
|---|---|
| Speakers | Two **Xiaomi Sound Pocket** in a stereo (TWS) pair |
| Their addresses | ending **7833** (paired 2026-09-22) and **78EC** (paired 2026-09-28) |
| Bluetooth adapter in use | Realtek, `USB\VID_0BDA&PID_C820` |
| Other adapters | a Generic (Actions) adapter, **disabled** (code 22), and an unplugged CSR dongle |
| Other audio | NVIDIA HDMI (monitor), Realtek headphone jack, USB webcam mic |
| Windows | 11, build 26200 |

## Symptoms as reported

1. The speakers switch off after 15-20 minutes even with Speaker Keeper running.
2. They switch off in the middle of a game, while the game is playing through them.
3. After using the speakers with a phone, the PC asks to pair them again, and after
   pairing again "Speaker Keeper stops working".

## Findings

| # | Finding | Status |
|---|---|---|
| F1 | The speaker has its own auto-off at **15 minutes**, and it fires while Speaker Keeper is holding a stream | **Confirmed** |
| F2 | In games, voice chat opens the speaker's microphone, which puts it in **call mode**, and in call mode the speaker switches off 15 minutes later even with the game audible through it | **Confirmed by elimination** (2026-10-01): two drops match a voice-chat mic switch to the second, and with call mode made impossible there have been no drops while the PC was awake |
| F3 | Digital silence in **music mode** keeps the speaker on past 15 minutes | **Confirmed** 2026-10-01: 30m55s of nothing but Speaker Keeper's silence (18:49:02 to 19:19:57), no drop. It went only when the PC slept |
| F4 | The "pair again" prompt is the *other* speaker of the pair, not a lost pairing | **Likely** |
| F5 | The app's drop warning blames the Bluetooth adapter, wrongly | **Confirmed** |
| F6 | Holds that lasted hours were times real audio was playing | **Assumed**, never measured |
| F7 | The display turns off after **15 minutes** idle, the same number. The user suspects the monitor sleeping takes the speaker with it | **Refuted** 2026-10-01: display off 18:25:41 to 18:48:10 (23 min) and 19:04:47 to 19:19:34, speaker on throughout |

> **Conclusion as of 2026-10-01:** Speaker Keeper's silence works. The one way these
> speakers switch off while the PC is awake is **call mode**: an app holding the
> speaker's microphone makes Windows suspend the music channel the silence travels on,
> and nothing an app can send reaches the speaker until the microphone closes. The
> parked tone ladder is therefore not needed for this speaker. The fix is keeping
> speakers out of call mode.
>
> **Meetings are the same thing** (reported 2026-10-01: off during Google Meet, Zoom,
> Teams). A meeting opens a microphone exactly as voice chat does. Browser meetings use
> Windows' *ordinary* default microphone; Teams, Zoom and Discord use the *calls* one.
>
> **Decided 2026-10-01: speakers must stay on while the PC is awake, microphone or not.**
> Since nothing can keep a speaker on once an app holds its microphone, that means
> keeping it out of call mode by default for everyone, without disabling anybody's
> hardware: *Keep speakers out of calls* (on by default) moves both microphone roles,
> and a hands-free output in either output role, off kept-awake loudspeakers, and a
> notification names any app that picks the speaker's microphone itself. *Turn off
> speaker microphones* (1.6.0, opt-in) remains the guarantee. Both are tested on the
> machine this was found on, roles moved within a second; see TECHNICAL.md.

### F7. The display's 15 minutes

`powercfg` shows the display timeout at 900 s on mains power (600 s on battery). The
same number as the speaker drops is a coincidence worth ruling out, and the user
suspected it independently.

Evidence against it for most drops: a sleeping monitor takes its HDMI audio output
(`Screen Audio (NVIDIA ...)`) away. Today that shows as `default output changed ->
unknown` 15 minutes after boot at 04:31:40, and as 25-second flaps where the lock screen
wakes it briefly. But at most past drops, Windows fell back to `Screen Audio` **in the
same second** the speaker went, so the monitor was still on: 2026-10-01 00:48:30, and
2026-09-30 22:13:27, 22:30:33 and 22:45:59, mid-game. The tray's log also cannot see the
display at all, which is why it is logged now (`display off` / `display on` /
`display dimmed`, plus `PC locked` / `PC unlocked`, since a locked PC turns its display
off after one minute rather than fifteen).

The two 15-minute clocks also start differently, which is how Test 1 tells them apart:
the speaker's counts from its last sound (or from connecting), the display's from the
last keyboard or mouse input.

### F1. The speaker's own 15-minute timer

Measuring every hold in the log, from `keeping X awake` to that speaker's
`stream lost 0x88890004`, a dozen end within seconds of 15 minutes:

```
2026-09-30 21:43:15  held 14m57s
2026-09-30 21:58:31  held 14m55s
2026-09-30 22:15:37  held 14m55s
2026-09-30 22:31:03  held 14m55s
2026-09-30 22:46:19  held 14m56s
... plus 09-22 01:12 (14m54s), 09-22 03:38 (14m59s), 09-23 18:33 (14m58s),
    09-24 18:16 (14m57s), 09-27 12:20 (14m59s), 09-29 00:05 (14m59s), 09-30 01:59 (15m20s)
```

A Bluetooth link failing at random does not keep time to the second. This is a timer
in the speaker's firmware. Xiaomi documents the same behaviour on a sibling model (the
Sound Party): it powers down after about 15 minutes connected with no audio playing.
`0x88890004` (`AUDCLNT_E_DEVICE_INVALIDATED`) is just Windows reporting the speaker gone.

The awk one-liner that produces this table is at the end of this file.

### F2. Call mode, from voice chat

The strongest evidence. Windows records, per app, the last time it **started** using a
microphone (`HKCU\...\CapabilityAccessManager\ConsentStore\microphone`). Two of those
land on a speaker drop to the second:

| Speaker went off | App started using a microphone |
|---|---|
| 2026-09-27 12:35:25, 14m59s after connecting | Discord, 12:35:26 |
| 2026-09-30 23:01:15, 14m56s after connecting | CS2, 23:01:15 |

The reading: the app already had the **speaker's** microphone open, the speaker died,
and the app reopened the next microphone a second later, which reset its timestamp.

And the reason it had the speaker's microphone: on this PC Windows' default microphone
**for calls** is `Headset Microphone (2- Xiaomi Sound Pocket)`. Discord, Teams and game
voice chat use the calls microphone, not the ordinary default (the webcam here).

> **This is easy to miss, and was.** Windows keeps two default microphones. Settings >
> Sound > Input shows only the ordinary one, which here is correctly the Anker webcam,
> so checking there says "the speaker's mic isn't the default" and is right. The other
> one, the *Default Communication Device*, only shows in the old Sound control panel
> (`mmsys.cpl` > Recording), and that is what voice chat opens. Windows hands that role
> back to the speaker **every time it reconnects**: it was the webcam at 00:55 with the
> speaker off, and the speaker's again at 17:01 once it was back. Changing it by hand
> therefore does not stick. Taking the microphone away from the speaker does (below).

Opening a Bluetooth speaker's microphone moves it off its music profile (A2DP) onto the
hands-free one (HFP): 16 kHz mono, call quality. The working theory is that the
speaker's auto-off does not count call audio as "playing", so it switches off 15
minutes after the last music-mode audio **even while the game is audible through it**.
That matches symptom 2 exactly. Test 2 is what proves or disproves it.

This also explains why the tone experiment (below) could never have worked for these
drops: in call mode, whatever Speaker Keeper sends on the music channel is not heard.

### F3. Does silence work in music mode?

Unknown until Test 1. If it does, the silent stream was never the problem, and every
15-minute drop is F2 (or real silence at a time the speaker was not being held).

### F4. "Asks me to pair again"

The two speakers are separate Bluetooth devices with the same name. In a stereo pair
only one of them, the primary, talks to the PC. After the pair is used with a phone, the
other one can come back as primary, and if the PC has never paired *that* one, Windows
offers to pair a "new" Xiaomi Sound Pocket. That is what happened on 2026-09-28 15:33:
`78EC` was paired for the first time, and the log shows `2- Xiaomi Sound Pocket` from
that moment.

Both are paired now, and on 2026-09-30 01:59 the PC connected to `7833` without asking.
**Neither entry in Bluetooth settings is a duplicate; removing one brings the prompt
back.** If it asks again, the last four characters Speaker Keeper shows next to the name
tell you which speaker it is.

Pairing is not what breaks the keep-alive: the 15-minute drops are in the log from
2026-09-22, before and after the 09-28 pairing, on both speakers.

### F5. The warning points at the wrong thing

`Health.Advice()` says *"the drops are the Bluetooth connection itself, not the app"*
and suggests removing the second adapter. Here neither is true: the drops are the
speaker powering itself off, and the second adapter is code 22, which means **disabled**,
not broken. A disabled adapter cannot interfere. The warning needs to know about F1/F2
and treat code 22 as absent.

## Timeline of what has been tried

| When | What | Result |
|---|---|---|
| 1.0 - 1.2 | A media player looping `silent.wav` | Drops continued |
| 1.3.0 | Raw WASAPI stream of digital silence | Drops continued |
| 1.5.0 | Warn about flapping connections and second adapters | Pointed at the wrong cause (F5) |
| 2026-09-22 01:44 | Dev build sending an "inaudible tone" | Speaker still off at 15:00 from connecting. Level not recorded |
| after 1.5.1 | Uncommitted "tone ladder": silence, then -70 to -45 dBFS, one step per two drops | Never built. Parked on 2026-10-01 as `git stash` *"parked: tone ladder, never built"*: it cannot help in call mode (F2), and it costs two power-offs per step. Bring it back with `git stash list` / `git stash pop` only if Test 1 fails |
| 2026-10-01 | `Activity` logging in the app, `tools/AudioProbe` | See below. Makes every future drop explain itself |

## Tests

Watch everything with `tools\AudioProbe.exe watch <file>` while a test runs; it logs
default devices, which apps play or record where, formats, volume, and a per-minute
"loudest / seconds of sound" line per output.

### Test 1: silence only, music mode, no microphone

- **Setup:** Speaker Keeper 1.5.1 holding `78EC`. Nothing else playing, no app
  recording. The probe confirms `sound in 0s of 60` every minute from 00:44:00.
- **Pass:** the speaker is still connected after 00:59:30.
- **Attempt 1, 2026-10-01: inconclusive.** The speaker went at **00:48:30**, 28m51s
  after connecting at 00:19:39. Nothing was playing and no microphone was open for
  the 4.5 minutes the probe watched, but the probe only started at 00:44. If the timer
  counts from the last real sound, that sound was at 00:33:30, before anyone was
  watching. Not proof either way. Lesson: **start watching before the speaker
  connects**, and time from the connection.
- **Attempt 2, 2026-10-01:** the PC restarted for a Windows update at 04:14, so the
  installed 1.5.1 was running (no `Activity`). Speaker connected **16:55:43**. Probe
  watching from 17:01:03: `sound in 0s of 60` every minute, nothing recording from the
  speaker. Display last touched about 17:00. Reading it:
  - off at 17:10:43 ±30s: the speaker's timer from connecting, and silence is ignored;
  - off by 17:16: also silence ignored (the last unseen sound was before 17:01:03);
  - off together with `Screen Audio` vanishing: the display (F7);
  - still on after 17:16:30: silence works in music mode (F3 confirmed).
  - **Result: inconclusive again, by 20 seconds.** The speaker stayed on for 38 minutes
    held, so there is **no timer of "15 minutes from connecting, whatever happens"**.
    But real sound kept landing just before each deadline: Chrome and a chime at
    17:04:38-17:05:01, a chime at 17:17:34, and Chrome audio at 17:32:18 (-2 dB). That
    left a longest pure-silence stretch of **14m40s**, 20 seconds short. The display
    stayed on throughout (HDMI audio never left), so F7 was not tested either.

> **Testing trap: Claude Code's turn-end chime plays through the speaker.** Every time
> an assistant turn ends, `powershell.exe` and *Windows sounds* play for about 2
> seconds at -21 dB through the default output, which is the speaker. The transcript
> times match exactly (turns ended 17:04:57 and 17:17:33; probe saw sound at 17:04:58
> and 17:17:34). That resets the speaker's timer. **During a silence test, wait in a
> foreground command and do not end the turn**, or turn the terminal bell off.

### Test 1, next: let the log run it

Sitting a user in silence for 16 minutes has failed twice on stray sounds. The
`Activity` build makes it unnecessary. Every `disconnected:` line states how long after
the last sound the drop came, and the chime and every other app count as sound, so
ordinary use answers F3 the first time the speaker sits idle while held:

- a `disconnected:` line ending `... 15-minute auto-off, not a lost connection` **with no
  call mode in it** = silence is ignored in music mode. F3 refuted, and the parked tone
  ladder becomes the next thing to try;
- an `activity ... silent apart from Speaker Keeper` run of 4+ summaries (20+ minutes)
  with no disconnection = silence works. F3 confirmed.

### Test 2: call mode

- **Setup:** `AudioProbe record "Xiaomi"` opens the speaker's microphone the way voice
  chat does, while audible sound plays to it.
- **Pass/fail:** off about 15 minutes later = F2 confirmed.
- **Result:** *not run yet*

## Fixes, once the tests are in

- **Decided by the user, 2026-10-01: the speakers are for sound output only**, so their
  microphone goes, whatever Test 2 would have shown. That makes Test 2 optional: if the
  mid-game drops stop once there is no microphone to open, F2 is confirmed in practice.
  - **Now a setting: Settings > Speakers > *Turn off speaker microphones*** (from the
    2026-10-01 build). Off by default for everyone else. When on, a SYSTEM task switches
    the Hands-Free profile off again on every re-pair, triggered by the PnP event Windows
    writes when it sets up the new node, so nothing has to be re-run by hand. Details in
    TECHNICAL.md, *Turning off speaker microphones*. The dev build was installed on
    2026-10-01 17:48 to test it, since the task only ever points at a Program Files copy.
  - **The manual version: `tools/speaker-output-only.ps1 "Xiaomi Sound Pocket"`**. It
    was run at 17:41 and worked: the speaker's microphone vanished, Windows moved the
    calls microphone to the Anker by itself, and music carried on. Undone at 17:48 so the
    setting could take over and record what it changed. It switches off
    the *Hands-Free AG* node of every paired speaker with that name, both 7833 and
    78EC. The speaker then publishes no microphone and can never enter call mode.
    `-Restore` undoes it, and re-pairing a speaker brings the profile back, so run it
    again after any re-pair. Same as unticking *Handsfree Telephony* under Devices and
    Printers > the speaker > Properties > Services.
  - **Changing the Default Communication Device by hand does not stick**: Windows gives
    it back to the speaker on every reconnect (see F2).
- App-side:
  - Log it the moment it happens (see the log section).
  - Warn when the calls microphone is a speaker Speaker Keeper holds.
  - Stop the drop warning blaming the adapter (F5).

## Where it stands (2026-10-01, after 1.6.0)

- **Released as 1.6.0:** the `Activity` logging below and the *Turn off speaker
  microphones* setting. On the machine this was found on, the setting has been on
  since 17:50. Both Xiaomi microphones are off, and Windows' calls microphone is the
  Anker webcam. A simulated re-pair (node re-enabled, task run) was switched back off
  by the SYSTEM task within a second.
- **F3 answered by ordinary use**, as *Test 1, next* hoped: see the findings table.
- **A 1.6.0 bug, found and fixed:** `activity: sample failed: COM object that has been
  separated from its underlying RCW`, once after sleep and once on a reconnect. Windows
  gives every caller in a process the same `MMDeviceEnumerator`, so the CLR gives every
  caller the same wrapper (`ReferenceEquals` is true), and `Silence` calling
  `FinalReleaseComObject` on its copy when a stream ended killed `Activity`'s. Both now use
  `ReleaseComObject`. Verified by ending a stream on purpose: no failure.
- **Things that look like bugs and are not:**
  - *No permission prompt when changing the setting.* This PC has
    `ConsentPromptBehaviorAdmin = 0` ("elevate without prompting"), so Windows grants
    admin silently. The change still happens and is logged.
  - *MIXER RXD lost its microphone too.* It reports Class of Device minor 7 (portable
    audio), which the setting counts as a loudspeaker. Expected; the setting covers
    every paired speaker.
  - *A notification "AudioProbe is using your speaker as a microphone"*: that is the
    bench tool opening the microphone during a test, not a real app.
  - *Sound in a "silent" window from `OpenWhispr.exe`* (dictation app) or
    `powershell.exe` + *Windows sounds* (Claude Code's turn-end chime). Both play
    through the default output and reset the speaker's timer.

## What the log records now (from 1.6.0)

Before this, the log recorded only what Speaker Keeper did, so every round of this
investigation started by guessing what the speaker had been doing. `Activity` watches
every output and microphone once a second and writes down, as it happens:

| Line | Answers |
|---|---|
| `connected: Speakers (X), 44100 Hz stereo, volume 50%` | When a Bluetooth output or microphone appeared, and in what state |
| `cs2.exe playing to Speakers (X) (from 21:43:20)` / `stopped playing` | Which app was playing where. Logged once an app has played or stopped for 10s, so pausing a video does not flood the log |
| `cs2.exe opened the microphone Headset Microphone (X) - that speaker is in call mode...` | **The F2 event.** Who opened a speaker's microphone, and when |
| `default microphone for calls -> ...` | The Windows setting behind F2, at startup and whenever it changes |
| `Speakers (X): format 44100 Hz stereo -> 16000 Hz mono` | The speaker changing profile |
| `volume Speakers (X): 50% -> 0%` | A muted or zeroed output looks exactly like silence |
| `activity Speakers (X), last 5 min: sound for 212s, loudest -18 dB, from cs2.exe; music mode...` | Every 5 minutes: whether the speaker was actually hearing anything. `silent apart from Speaker Keeper` is Test 1's condition |
| `disconnected: Speakers (X) after 14m55s connected; last sound 3s before, from cs2.exe; in call mode since 21:43:31 (cs2.exe had its microphone open); last sound in music mode 14m50s before; that is 14m50s after its last music-mode sound, which is the speaker's own 15-minute auto-off, not a lost connection` | **The whole diagnosis in one line**, written at the moment of the drop |
| `stream lost ... , held for 14m55s` | How long Speaker Keeper held it. A drop at the same number of minutes every time is a timer |
| `letting X sleep - <reason>` | Always with a reason now. A speaker powering off used to show up as `letting X sleep` with none, at 2026-09-30 23:01:15 |
| `display off` / `display on` / `display dimmed`, `PC locked` / `PC unlocked` | F7: whether the screen sleeping lines up with a drop |

The log now rotates at 4 MB and keeps three old files (`SpeakerKeeper.log.1` to `.3`),
so a pattern across days is still there to find.

## Reading the log for this

`%LocalAppData%\Speaker Keeper\SpeakerKeeper.log`. How long each hold lasted:

```bash
awk 'function ts(d,t,a,b){split(d,a,"-");split(t,b,":");return mktime(a[1]" "a[2]" "a[3]" "b[1]" "b[2]" "b[3])}
/keeping .* awake/{n=$0;sub(/.*keeping /,"",n);sub(/ awake.*/,"",n);s[n]=ts($1,$2);w[n]=$1" "$2}
/: stream lost/{n=$0;sub(/^[0-9-]+ [0-9:]+  /,"",n);sub(/: stream lost.*/,"",n);if(n in s){d=ts($1,$2)-s[n];printf "%s  %-30s held %3dm%02ds\n",w[n],n,d/60,d%60;delete s[n]}}' SpeakerKeeper.log
```

A hold ending at 14m50s-15m20s is the speaker's timer. Anything else is something else.

Which apps used a microphone last, and when (PowerShell):

```powershell
$r='HKCU:\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone'
Get-ChildItem "$r\NonPackaged",$r | % { $p=gp $_.PSPath; if($p.LastUsedTimeStart){ [pscustomobject]@{App=$_.PSChildName;
  Start=[DateTime]::FromFileTime($p.LastUsedTimeStart); Stop=[DateTime]::FromFileTime($p.LastUsedTimeStop)} } } | sort Start -desc
```

It only keeps the **last** use per app, so check it soon after a drop.
