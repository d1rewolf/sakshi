using Sakshi.Core.Paths;

namespace Sakshi.Platform;

/// <summary>How each OS opens a folder or a file in the user's default application.</summary>
public static class DesktopOpen
{
    public static (string File, IReadOnlyList<string> Args) Command(HostOs os, string target) => os switch
    {
        HostOs.MacOS => ("open", [target]),
        HostOs.Windows => ("explorer.exe", [target]),
        _ => ("xdg-open", [target]),
    };
}
