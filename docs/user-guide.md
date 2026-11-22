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
| **Live transcript** | While recording: a window showing what is being said, about 10 seconds behind (see below) |
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

While recording, choose **Live transcript** in the tray menu. A window shows what
is being said, each line with its time in the meeting, usually 6 to 11 seconds
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
- It needs OBS to record as **Hybrid MP4** or **MKV** (OBS: Settings → Output →
  Recording Format), because a plain MP4 cannot be read until it is finished.
  The window tells you if OBS is set to plain MP4.
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

Starting and stopping from a key is quicker than the menu. Bind this command to
a key in your desktop's keyboard settings:

```sh
sakshi toggle
```

It starts a recording, or stops the one in progress. On Omarchy, add a binding
in `~/.config/hypr/bindings.lua`. On macOS and Windows, use a shortcut tool you
already have (for example macOS Shortcuts or Windows PowerToys); give the full
path to `sakshi` on macOS. The other commands that talk to the running tray are
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

The other settings (OBS connection details, the share picker on Linux) rarely
need changing.

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
