# Installing Sakshi

Sakshi records a meeting window with OBS and turns it into `meeting.md`, a
transcript with screen frames. Download the installer for
your system below; each link fetches the newest release. Older versions are
on the Releases page (<https://github.com/d1rewolf/sakshi/releases>).

| OS | File | Needs |
| --- | --- | --- |
| Windows 10 1903+ (64-bit) | [`sakshi-win-Setup.exe`](https://github.com/d1rewolf/sakshi/releases/latest/download/sakshi-win-Setup.exe) | OBS 32+ |
| macOS 13+ (Apple silicon) | [`sakshi-osx-Setup.pkg`](https://github.com/d1rewolf/sakshi/releases/latest/download/sakshi-osx-Setup.pkg) | OBS 32+ |
| Linux (x86-64) | [`sakshi.AppImage`](https://github.com/d1rewolf/sakshi/releases/latest/download/sakshi.AppImage) | OBS 32+, a tray |

The installers include ffmpeg, which Sakshi uses to read recordings, but not
OBS, which does the recording. When Sakshi first starts, its setup window lists
what is missing and the command that installs each piece (see "Check the
install" below).

The builds are **not signed**, so each OS warns you the first time. The steps
below get past the warning. Nothing is wrong with the file.

There is no automatic update. To update, install the newer release the same way
(see "Updating").

Once it is installed, [the user guide](user-guide.md) explains how to record,
find and share meetings.

## Windows

1. Download `sakshi-win-Setup.exe` and run it.
2. SmartScreen says "Windows protected your PC": choose **More info**, then
   **Run anyway**.
3. The installer needs no admin rights. It also installs the Visual C++
   runtime if it is missing.
4. Open a **new** terminal (PowerShell or cmd) so it sees the updated PATH,
   then run the checks below. The installer puts the `sakshi` command on your
   PATH and adds a Start menu entry.

## macOS

1. Download `sakshi-osx-Setup.pkg` and open it.
2. macOS refuses it as from an unidentified developer:
   - **macOS 15 or newer:** right-click then Open no longer works. Open
     **System Settings, Privacy & Security**, scroll to the message about
     `sakshi-osx-Setup.pkg` and choose **Open Anyway**, then confirm.
   - **macOS 13 or 14:** right-click the file and choose **Open**.
   - Or, from a terminal, remove the quarantine mark and open it again:
     `xattr -dr com.apple.quarantine ~/Downloads/sakshi-osx-Setup.pkg`
3. Follow the installer. It puts `Sakshi.app` in `/Applications`, or in
   `~/Applications` if you choose "Install for me only".
4. OBS: install `OBS.app` from <https://obsproject.com>, open it once and
   quit it.
5. OBS needs the Screen Recording and Microphone permissions (System
   Settings, Privacy & Security; on macOS 15 Screen Recording is called
   "Screen & System Audio Recording"). The setup window has an
   **Open settings** button for each. If macOS asks when OBS first records,
   choose **Allow**.

## Linux

1. Download `sakshi.AppImage`, make it executable and run it once from a
   terminal (or the file manager):

   ```sh
   chmod +x sakshi.AppImage
   ./sakshi.AppImage
   ```

   That starts the tray. Keep the file where you want it to stay (for example
   `~/Applications/sakshi.AppImage`).
2. On each tray start, sakshi writes a menu entry
   (`~/.local/share/applications/sakshi.desktop`), the icon, and a link
   `~/.local/bin/sakshi` that points at the AppImage. If you move or rename
   the AppImage, run it once from its new place and they are repointed. An
   existing `~/.local/bin/sakshi` that is not a link is left alone.
   Make sure `~/.local/bin` is on your PATH to type `sakshi` in a terminal.
3. The AppImage does not need `libfuse2`, but the system needs `fusermount`
   (the `fuse3` package on Ubuntu). Without it, run
   `./sakshi.AppImage --appimage-extract-and-run`, or set
   `APPIMAGE_EXTRACT_AND_RUN=1`.
4. OBS 32+: on Ubuntu 24.04 use the OBS PPA
   (`sudo add-apt-repository ppa:obsproject/obs-studio`), then
   `sudo apt install obs-studio`. GNOME needs the AppIndicator
   extension for the tray icon (Ubuntu enables it by default).

## Check the install

The first time the installed app starts, a **Set up Sakshi** window lists what
it needs. Each missing piece has its fix command and a **Copy** button: run the
command in a terminal, then choose **Check again**. The window also opens at
any later start where something is missing, and **Check setup…** in the tray menu
opens it whenever you like.

From a terminal, run `sakshi doctor`. It checks OBS, ffmpeg and the rest, prints one line per
check, and one fix line for each thing that is missing. It exits 0 when
everything is fine and 1 otherwise.

- Windows: `sakshi doctor` in a new terminal.
- Linux: `./sakshi.AppImage doctor`, or `sakshi doctor` once the tray has run
  and `~/.local/bin` is on your PATH.
- macOS: the command is not on PATH. Run
  `/Applications/Sakshi.app/Contents/MacOS/sakshi doctor` (use
  `~/Applications/...` if you installed there). To type `sakshi`, add this to
  `~/.zshrc`:

  ```sh
  alias sakshi=/Applications/Sakshi.app/Contents/MacOS/sakshi
  ```

After the first
transcription starts, a speech model (about 150 MB) and two models for telling
speakers apart (about 33 MB) are downloaded, so the first one needs internet
and takes longer.

## Where things live

| | Windows | macOS | Linux |
| --- | --- | --- | --- |
| Config (`config.json`) | `%APPDATA%\Sakshi` | `~/Library/Application Support/Sakshi` | `~/.config/sakshi` |
| State and log | `%APPDATA%\Sakshi\state` | `~/Library/Application Support/Sakshi/state` | `~/.local/state/sakshi` |
| Data (models, index, voice library) | `%LOCALAPPDATA%\Sakshi` | `~/Library/Application Support/Sakshi/data` | `~/.local/share/sakshi` |

Meetings are in `~/meetings` unless `meetingsDir` in `config.json` says
otherwise. Uninstalling never touches any of these.

## Start at login

A new install starts the tray at login (on Linux, from the first time the
AppImage's tray starts). To turn it off, untick **Start at login** in the tray
menu. It stays off, including after updates and reinstalls. The entry it
controls is:

- Windows: the `Sakshi` value under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
- macOS: `~/Library/LaunchAgents/io.github.d1rewolf.sakshi.plist`
- Linux: `~/.config/autostart/sakshi.desktop` (under `$XDG_CONFIG_HOME` if set)

## Updating

There is no automatic update. Get the newer installer (the same way you got
this one) and run it the same way (Windows and macOS replace the old copy in place). On
Linux, replace the AppImage with the new one and start it once.

Stop any recording and let transcription finish first: the installer closes
the running tray. (On Linux, quit the tray yourself before you replace the
AppImage.)

## Uninstalling

Your config and meetings are kept in every case (see "Where things live").

- **Windows:** Settings, Apps, Installed apps, Sakshi, Uninstall. That removes
  the app, the login entry and the PATH entry it added, and forgets your Start
  at login choice, so a later reinstall starts at login again.
- **macOS:** delete `Sakshi.app` from `/Applications` (or `~/Applications`)
  and `~/Library/LaunchAgents/io.github.d1rewolf.sakshi.plist`.
  Optional: `~/Library/Application Support/Sakshi/state/autostart-chosen`
  remembers your Start at login choice; delete it too if you want a reinstall
  to start at login again.
- **Linux:** delete the AppImage, `~/.local/share/applications/sakshi.desktop`,
  `~/.local/share/icons/hicolor/256x256/apps/sakshi.png`,
  `~/.config/autostart/sakshi.desktop` and `~/.local/bin/sakshi`.
  Optional: `~/.local/state/sakshi/autostart-chosen` (under `$XDG_STATE_HOME`
  if set) remembers your Start at login choice; delete it too if you want a
  reinstall to start at login again.

## Omarchy

Run the AppImage once, so `~/.local/bin/sakshi` exists. Then:

- Bind the chord in `~/.config/hypr/bindings.lua` to
  `home .. "/.local/bin/sakshi toggle"`.
- Set up the share picker, which lists meeting windows first. The setup
  window (**Check setup…** in the tray menu) gives you a command that does all of
  this; by hand, make a small script, for example
  `~/.local/bin/sakshi-share-picker`:

  ```sh
  #!/bin/sh
  exec ~/.local/bin/sakshi share-picker "$@"
  ```

  and name it as `custom_picker_binary` in `~/.config/hypr/xdph.conf`, then
  `systemctl --user restart xdg-desktop-portal-hyprland`.
- The AppImage turns on its own login entry at first start. Keep a
  `launch_on_start` line only if you run from a source build.

All of this is optional: Sakshi records without it.

### If the picture freezes during a recording

Under Hyprland, resizing the recorded window (making it fullscreen, for example)
can leave OBS stuck on the last picture while the sound carries on. Sakshi checks
every 5 seconds. When the picture has not changed for 10 seconds and no window
is the size OBS shows, it reconnects the capture to the same window without
asking. About 2 seconds of picture and sound are lost each time, and you get a
"Screen capture froze" notification.

If that does not bring the picture back, you get "Screen capture is frozen".
Choose **Re-pick window** in the Sakshi menu, or run `sakshi repick`, and pick
the window again. The recording keeps going either way.
