# Using Sakshi

Sakshi records a meeting window and turns the recording into `meeting.md`: a
transcript of everything said, with a picture of the screen each time the
slides or the shared screen change. You can read it, search it, or hand it to
an LLM to summarise.

Most LLMs cannot read a video file, and those that can find an hour of video
slow and costly to read. `meeting.md` is text and a few pictures, so any LLM
can read it, and it stays on your computer until you choose to send it.

Sakshi uses OBS to do the recording. You never need to open OBS yourself:
Sakshi starts it, records, and stops it.

If Sakshi is not installed yet, see [Installing Sakshi](install.md) first.

## The tray icon

Sakshi lives in the tray (Windows: the notification area at the bottom right,
which may hide it behind the ^ arrow; macOS: the menu bar at the top right;
Linux: your bar's tray). The icon is an eye, and its pupil shows what Sakshi is doing:

- **Grey pupil:** idle, ready to record.
- **Blinking red pupil:** recording.
- **Amber ring:** starting a recording, or waiting for you to name the one you stopped.
- **Blue pupil:** a transcript is being made in the background. You can record meanwhile.

Its menu has:

| Item | What it does |
| --- | --- |
| Status line | What Sakshi is doing now, for example how long it has been recording |
| **Start recording** / **Stop recording** | Starts or stops a recording (the label follows what is happening) |
| **Live transcript** | While recording: a window showing what is being said, a few seconds behind (see below) |
| **Re-pick window** | Linux under Hyprland, while recording: choose the recorded window again without stopping |
| **Open meetings folder** | Opens the folder your meetings are saved in |
| **Open config** | Opens the settings file (see [Settings](#settings)) |
| **Setup…** | Shows what Sakshi still needs, with the commands to install it |
| **Start at login** | Whether Sakshi starts when you log in |
| **Quit** | Closes Sakshi; a transcription that is running carries on |

On Omarchy a left click on the icon starts or stops a recording. On Windows,
right-click the icon for the menu; on macOS (and on some Linux desktops) a
click opens the menu.

## Recording a meeting

1. Join the meeting as usual.
2. Choose **Start recording** (or press your keyboard shortcut, see below; on
   Omarchy you can also click the icon).
3. Sakshi asks which window to record. Pick the meeting window: Sakshi lists
   Zoom, Teams, Google Meet, Webex and GoToMeeting windows first.
4. A notification says the recording started. The icon blinks while it records.
5. When the meeting ends, choose **Stop recording**.

Every 30 minutes Sakshi asks whether you are still recording, in case you
forgot to stop. Choose **Keep recording**, or **Stop recording** to stop now.
If you do not answer within 10 minutes, it keeps recording.

**Tips**

- Put the meeting window at the size you want before you start. On Linux under
  Hyprland, resizing it (for example making it fullscreen) can freeze the
  picture. Sakshi notices within about 15 seconds and reconnects, losing about
  2 seconds; if that does not work it asks you to choose **Re-pick window**.
- Sakshi records the meeting window, not your whole screen, so other windows
  stay private.
- On macOS, OBS needs the Screen Recording and Microphone permissions. The
  setup window has a button that opens the right settings page.

## Watching the transcript live

While recording, choose **Live transcript** in the tray menu, or set
`liveTranscript` to `true` in the settings to have it open by itself at the
start of every recording (it opens without taking the keyboard from your
meeting). A window shows what
is being said, each line with its time in the meeting, usually 3 to 6 seconds
behind the speaker. It follows the newest line; scroll up to read back, and it
stops following until you scroll to the bottom again. **Copy all** puts
everything so far on the clipboard.

- Opened partway through a meeting, it starts from the beginning and catches
  up, lines appearing as it goes. On a fast computer 20 minutes of meeting take
  about a minute to catch up; on an ordinary laptop, a few minutes.
- Closing the window does not stop it: open it again to see everything so far.
- Only you see it, on your computer.
- When the recording stops, the window says so; if it was still catching up, it
  stops there. The live text is a preview: the
  full transcript, which is more accurate and has the screen pictures, is still
  made afterwards in `meeting.md`.
- It needs OBS to record as Hybrid MP4 or MKV (`recordFormat`, Hybrid MP4
  unless you change it), because a plain MP4 cannot be read until it is
  finished. The window tells you if the format is plain MP4.
- The speech recognition uses at most half of your computer's processor cores,
  so the call stays smooth.

## After you stop

Sakshi asks three short questions. Each window closes on its own after
15 minutes and takes the safe answer.

1. **Recording stopped: Save it / Don't save.** Choosing **Don't save** asks
   once more before it moves the video to the trash, so a slip of the mouse
   cannot lose a meeting.
2. **Meeting name.** Type a name and press Enter, for example `weekly
   standup`. If you press Esc twice, the meeting is named after the time it
   stopped, such as `10-45`.
3. **Frames to keep.** How many screen pictures go into `meeting.md`:

   | Choice | Use it for |
   | --- | --- |
   | **Changes** (the usual) | Slides and screen shares: a picture each time the screen settles on something new, such as the next slide |
   | **Scene cuts** | Video that keeps moving, which "changes" skips because it never holds still |
   | **None** | Transcript only; the fastest |
   | **All** | Every frame; only for troubleshooting |

Sakshi then moves the video into your meetings folder and transcribes it in the
background. You can start the next recording straight away. A notification
says **Transcript ready** when `meeting.md` is done; this takes a few minutes
for a one-hour meeting. The very first transcription also downloads the speech
model (about 150 MB), so it needs the internet and takes longer.

## What you get

Meetings are filed by day, in `~/meetings` unless you change it:

```
meetings/
  2026-11-20-Fri/
    weekly_standup/
      weekly_standup.mp4   the recording
      meeting.md           transcript with screen pictures: the file to read or share
      frames/              the screen pictures meeting.md shows
      transcript.srt       subtitles, for a video player
      transcript.json      the transcript with timings, for programs
      audio.wav            the sound track the transcript was made from
      transcribe.log       what the transcription did, for troubleshooting
```

Spaces in names become underscores. `meeting.md` is plain Markdown: it opens in
any editor or Markdown viewer, and an LLM can read it, pictures included.

## Finding an old meeting

From a terminal:

```sh
sakshi find budget review
```

It searches every transcript, including archived ones, and lists each meeting
with the matching lines and their times. It also tries near spellings, since
speech-to-text sometimes mishears a word. When the list is cut short, add
`--all` to see every match.

On macOS the `sakshi` command is not on your PATH; ["Check the install"](install.md#check-the-install)
shows the full path and how to add a short alias.

## Older meetings are archived

Each time you start a recording, day folders older than 14 days move into
`meetings/archives`. Nothing is deleted, and `sakshi find` still searches them.
To keep them longer, change `keepDays` in the settings.

## Importing a video you already have

To turn an existing recording into a meeting:

```sh
sakshi import ~/Downloads/kickoff.mp4 "project kickoff"
```

Sakshi moves the video into the meetings folder (under the day the file was
last changed), asks which frames to keep, and transcribes it like a recording.
Leave out the name to be asked for one. Sakshi must be running (the tray), and
not recording.

## Keyboard shortcut

Starting and stopping from a key is quicker than the menu.

On Windows, press **Ctrl+Alt+R** while Sakshi is running. It starts a
recording, or stops the one in progress. To use another key, set `shortcut` in
the settings (below); if another program already uses the key, Sakshi says so
in a notification.

On Linux and macOS, bind this command to a key in your desktop's keyboard
settings:

```sh
sakshi toggle
```

On Omarchy, add a binding in `~/.config/hypr/bindings.lua`. On macOS, use a
shortcut tool you already have (for example macOS Shortcuts) and give the full
path to `sakshi`. The other commands that talk to the running tray are
`sakshi start`, `sakshi stop` and, on Linux under Hyprland, `sakshi repick`.

## Settings

**Open config** in the tray menu opens `config.json` (it is created with
comments the first time). Restart Sakshi (Quit, then start it again) after
changing it. The settings people change most:

| Setting | What it does | Default |
| --- | --- | --- |
| `meetingsDir` | Where meetings are saved | `~/meetings` |
| `keepDays` | Days before a day folder moves to `archives` | `14` |
| `archiveDir` | Where archived days go | `meetingsDir/archives` |
| `remindInterval` | Seconds between "still recording?" questions; `0` turns them off | `1800` (30 minutes) |
| `keepPrompt` | Ask "Save it / Don't save" after stopping; `false` always saves | `true` |
| `framesPrompt` | Ask which frames to keep; `false` always uses `transcribeArgs` | `true` |
| `transcribeArgs` | The frames setting used when the question is off or not answered | `-F changes -D 8` |
| `model` | Path to a different Whisper speech model file | downloaded `ggml-base.en.bin` |
| `liveTranscript` | Open the live transcript by itself when a recording starts | `false` |
| `shortcut` | Windows only: the key that starts or stops a recording, such as `Ctrl+Alt+R` (Ctrl, Alt or Win, plus a letter, a digit or F1–F24); `""` for none | `Ctrl+Alt+R` |

| `recordFps` | Frames per second of the recording | `15` |
| `recordMaxWidth`, `recordMaxHeight` | The largest recording; a smaller window is recorded at its own size | `1920`, `1080` |
| `recordFormat` | OBS's file format (`hybrid_mp4`, `mkv`, …); the live transcript needs one it can read while recording | `hybrid_mp4` |
| `recordEncoder` | The video encoder: `x264` (the processor), `nvenc` (NVIDIA), `amd`, `qsv` (Intel), `apple_h264`, or any OBS encoder name. Choose only one your computer has: OBS cannot record with a missing one. Blank: Apple's on a Mac, `x264` elsewhere | `""` |
| `recordFolder` | Where OBS writes the recording before Sakshi moves it; blank for OBS's usual folder | `""` |
| `obsWsRequirePassword` | Make OBS's remote control ask for a password (see below); `false` leaves OBS's own setting alone | `true` |

The other settings (OBS connection details, the share picker on Linux) rarely
need changing.

### Sakshi's own OBS settings

Sakshi records with its own OBS profile and scene collection, both called
"Sakshi", so your own OBS setup is never changed. The `record…` settings above
go into Sakshi's profile. When Sakshi starts OBS itself, it switches OBS to
Sakshi's settings for the recording and back to yours when it stops. If you
already had OBS open, Sakshi asks first, and you can say no. While OBS is
streaming, Sakshi cannot switch it and does not record.

Sakshi's profile saves a full picture (a keyframe) every 2 seconds. The live
transcript can only read the recording up to the last one, so this is what
keeps it a few seconds behind; OBS's own default was every 250 frames.

The recording is the size of the window you picked (scaled down to
`recordMaxWidth` × `recordMaxHeight` if it is bigger), so there are no black
bars. If you change the window's shape during the recording, the picture keeps
its starting size and the window is fitted inside it, with bars where the
shapes differ.

Sakshi's scene has your microphone and, on Windows and Linux, your computer's
sound (the other people in the meeting), both on the system's default devices.
Change them in OBS while it is on the Sakshi scene collection if you need
others. `obsScene` and `obsSource` name the scene and window source inside
Sakshi's collection.

Sakshi controls OBS through OBS's remote control (its WebSocket server). OBS
cannot limit that to your own computer: anyone on the same network can reach
it. So before it starts OBS, Sakshi makes sure the remote control asks for a
password, and gives it a random one if it has none. Sakshi reads the password
from OBS's settings, so there is nothing to type. If another tool of yours uses
OBS's remote control without a password, either give that tool the password
(OBS: Tools → WebSocket Server Settings → Show Connect Info) or set
`obsWsRequirePassword` to `false`.

### Sending finished meetings somewhere (hand-off)

A frames choice can also run a command once the transcript is ready, for
example to file the meeting in your notes or a ticket system. Add an entry to `framesModes` with a
`handOff` command:

```json
{ "glyph": "", "label": "Changes → Notes", "args": "-F changes -D 8",
  "description": "Slides and screen shares, then file it in your notes",
  "handOff": "file-meeting" }
```

Sakshi runs the command with the meeting folder added as its last argument and
writes what it printed to `handoff.log` in that folder. Commands are looked up
in the folders listed in `handOffPath` as well as your PATH.

## When something goes wrong

| What you see | What to do |
| --- | --- |
| Sakshi says something is missing | Choose **Setup…** in the tray menu, run the commands it shows, then **Check again**. `sakshi doctor` in a terminal shows the same checks. |
| The picture froze but the sound carried on | Linux under Hyprland: Sakshi usually fixes it by itself. If it asks, choose **Re-pick window**. |
| "No window selected — recording not started" | The window picker was closed without a choice. Start again and pick the meeting window. |
| **Transcription failed** | The video is safe in its meeting folder, and `transcribe.log` there says why. To try again, run `sakshi transcribe -F changes -D 8 -o FOLDER FOLDER/NAME.mp4`, with the meeting folder for `FOLDER` and the video's name for `NAME.mp4`. |
| **Hand-off failed** | The meeting is complete; only the extra command failed. See `handoff.log` in the meeting folder. |
| Anything else | Look in Sakshi's log, `sakshi.log` (see [where things live](install.md#where-things-live): it is in the state folder). |

The log and the meeting folders never leave your computer. Nothing in Sakshi
sends data anywhere, except the one-time speech model download.
