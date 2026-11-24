namespace Sakshi.App;

/// <summary>Which Sakshi this is, from the build: the version, and the commit it was built from when the build says.</summary>
internal static class AppVersion
{
    /// <summary>The informational version the SDK writes, e.g. "0.1.2+17a4472…" (the commit after the '+').</summary>
    private static string Informational =>
        typeof(AppVersion).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
        ?? "0.0.0";

    /// <summary>"0.1.2".</summary>
    public static string Number => Informational.Split('+')[0];

    /// <summary>"0.1.2 (17a4472)": with the short commit when the build carries one, so two builds can be told apart.</summary>
    public static string WithCommit => Describe(Informational);

    internal static string Describe(string informational)
    {
        var parts = informational.Split('+', 2);
        return parts.Length == 2 && parts[1].Length >= 7 ? $"{parts[0]} ({parts[1][..7]})" : parts[0];
    }
}
