using System.Security;

namespace Sakshi.Platform.Autostart;

/// <summary>
/// A LaunchAgent plist in ~/Library/LaunchAgents. Writing it is enough: launchd reads the folder at the
/// next login, so there is no launchctl call (and no running agent to unload on disable).
/// </summary>
public sealed class MacLaunchAgentAutostart(string home)
    : FileAutostart(Path.Combine(home, "Library", "LaunchAgents", "io.github.d1rewolf.sakshi.plist"))
{
    public override bool IsEnabled => System.IO.File.Exists(File);

    protected override string Content(string executable) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>io.github.d1rewolf.sakshi</string>
            <key>ProgramArguments</key>
            <array>
                <string>{SecurityElement.Escape(executable)}</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
            <key>ProcessType</key>
            <string>Interactive</string>
        </dict>
        </plist>

        """;
}
