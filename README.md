<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="packaging/sakshi-dark.svg">
    <img src="packaging/sakshi.svg" alt="Sakshi logo: an open eye" width="128" height="128">
  </picture>
</p>

# Sakshi

Sakshi (साक्षी, Sanskrit for "witness", one who sees with their own eyes)
records a meeting window and turns the recording into `meeting.md`: a
transcript of everything said, with a picture of the screen each time the
slides or the shared screen change, ready to read, search, or hand to an LLM. It runs in the tray on Windows, macOS
and Linux (Ubuntu and Omarchy), and uses OBS to record. When the screen should
stay out of it, **Record audio only** keeps just the sound (an `.m4a`) and
its transcript.

## Why

A meeting recording is a video, and most LLMs cannot read video: Claude and
OpenAI's models take text and images, not MP4s. The few that do take video
read it as a long run of frames plus the audio, which is slow and costly for an
hour-long meeting. Sakshi turns the meeting into something every LLM can read:

- **Text and a few pictures.** What was said, with a picture of the screen
  only when it changes. Much smaller than the video, so it is cheaper to send
  and fits in the LLM's context.
- **Any LLM.** `meeting.md` is plain Markdown; use it with whichever LLM or
  tool you like, including ones that run on your own computer.
- **Private until you share it.** Recording and transcription run on your
  computer. Nothing leaves it unless you send it somewhere.
- **Searchable.** `sakshi find` searches every meeting you have recorded.
- **Who said what.** Name a voice once and Sakshi labels it in later
  meetings. Voiceprints stay on your computer.

## Download

| System | Installer | Also needs |
| --- | --- | --- |
| Windows 10 1903+ (64-bit) | [sakshi-win-Setup.exe](https://github.com/d1rewolf/sakshi/releases/latest/download/sakshi-win-Setup.exe) | OBS 32+ |
| macOS 13+ (Apple silicon) | [sakshi-osx-Setup.pkg](https://github.com/d1rewolf/sakshi/releases/latest/download/sakshi-osx-Setup.pkg) | OBS 32+ |
| Linux (x86-64) | [sakshi.AppImage](https://github.com/d1rewolf/sakshi/releases/latest/download/sakshi.AppImage) | OBS 32+, a tray |

These links always fetch the newest release. Older versions are on the
[Releases page](https://github.com/d1rewolf/sakshi/releases). The builds
are not signed, so read the [install guide](docs/install.md) before the first
start.

## Using Sakshi

- **[Install it](docs/install.md):** the installer for your system, getting
  past the "unsigned app" warning, and the setup window that lists what else
  to install.
- **[Use it](docs/user-guide.md):** recording, naming and saving a meeting,
  what ends up in the meeting folder, searching old meetings, importing a
  video, the keyboard shortcut, settings, and what to do when something goes
  wrong.

---

The rest of this file is for people working on Sakshi's code.

Sakshi is written in C#/.NET 10 with Avalonia. It started as a bash script for
Omarchy; this rewrite keeps its behaviour and file formats.

## Layout

```
src/
  Sakshi.App/        the `sakshi` program: command line, tray icon, prompt windows,
                     the tray's command hub (Tray/), and the socket the keybinding uses
  Sakshi.Core/       config, state, meeting store, OBS client, start/stop/import,
                     transcription queue, hand-off, reminders
  Sakshi.Platform/   per-OS adapters (Linux/, MacOS/, Windows/): notifications, trash,
                     OBS launcher, window pick, share picker, start at login, doctor
  Sakshi.Tray/       Windows only: `sakshiw.exe`, the tray without a console window
  Sakshi.Transcribe/ ffmpeg + Whisper.net → transcript.json/.srt, frames, meeting.md
  Sakshi.Search/     `sakshi find` / `sakshi index` (SQLite FTS5)
tests/               xUnit v3 tests per project; TestSupport (fakes), TestHelper (a stand-in program)
tests/fixtures/bash/ copies of the bash config and transcribe-meeting, used as test references
docs/                user and install guides, release steps
```

## Build and test

```
dotnet build -warnaserror
dotnet test
```

CI runs the tests on Linux, macOS and Windows (`.github/workflows/dotnet.yml`).

## Installers and releases

The installers (`sakshi-win-Setup.exe`, `sakshi-osx-Setup.pkg`,
`sakshi.AppImage`) are built with Velopack by `.github/workflows/release.yml`.
A pushed `v*` tag publishes a release; running the workflow by hand only builds
the installers, as downloads on the run. Both are in `docs/releasing.md`.

## Run a build from source on Omarchy

Publish into a folder of your choice, for example `~/.local/lib/sakshi`
(rerun after every change, then choose Quit in the tray menu and start
`sakshi` again):

```sh
dotnet publish src/Sakshi.App -c Release -r linux-x64 --self-contained \
  -p:PublishReadyToRun=true -o ~/.local/lib/sakshi
```

A build from source has no ffmpeg of its own, so install it (`sudo pacman -S ffmpeg`).
Then wire it into the desktop:

1. Link `~/.local/bin/sakshi` to `~/.local/lib/sakshi/sakshi`.
2. Make `~/.local/bin/sakshi-share-picker` a two-line script,
   `exec ~/.local/lib/sakshi/sakshi share-picker "$@"`, name it as
   `custom_picker_binary` in `~/.config/hypr/xdph.conf`, then run
   `systemctl --user restart xdg-desktop-portal-hyprland`.
3. Bind a key to `sakshi toggle` in `~/.config/hypr/bindings.lua`, and start
   `sakshi` at login from `~/.config/hypr/autostart.lua`.
4. To keep the icon in the bar rather than in the tray drawer, add `"Sakshi"`
   to the `pinned` list of the `omarchy.tray` entry in
   `~/.config/omarchy/shell.json`.

## Status

Sakshi has had the most use on Omarchy (Arch Linux with Hyprland). The macOS,
Windows and Ubuntu versions are built and pass CI, but have had less use on
real machines so far; bug reports are welcome. The
installers are not signed, so Windows and macOS warn on first launch
(`docs/install.md` explains what to do).

## Licence

MIT; see `LICENSE`. Third-party licences, including the bundled icon font, are in `THIRD-PARTY-NOTICES.md`.
