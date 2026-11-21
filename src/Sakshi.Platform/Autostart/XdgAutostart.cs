namespace Sakshi.Platform.Autostart;

/// <summary>An XDG autostart entry, <c>&lt;configHome&gt;/autostart/sakshi.desktop</c>.</summary>
public sealed class XdgAutostart(string configHome) : FileAutostart(Path.Combine(configHome, "autostart", "sakshi.desktop"))
{
    public override bool IsEnabled =>
        System.IO.File.Exists(File) && !System.IO.File.ReadAllLines(File).Contains("X-GNOME-Autostart-enabled=false");

    protected override string Content(string executable) =>
        $"[Desktop Entry]\nType=Application\nName=Sakshi\nExec={QuoteExec(executable)}\nIcon=sakshi\nX-GNOME-Autostart-enabled=true\n";

    /// <summary>
    /// The Desktop Entry spec: quote the argument, backslash-escape " ` $ \ inside it, and double each of those
    /// backslashes again (the string-value escape is applied first), so a literal \ becomes four. % is a field code.
    /// </summary>
    internal static string QuoteExec(string path)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (var c in path)
            sb.Append(c switch
            {
                '\\' => @"\\\\",
                '"' or '`' or '$' => @"\\" + c,
                '%' => "%%",
                _ => c.ToString(),
            });
        return sb.Append('"').ToString();
    }
}
