using Sakshi.Core.Ports;
using Sakshi.Platform.Autostart;

namespace Sakshi.App.Install;

/// <summary>
/// What the installer does for the app: autostart on the first run, the CLI on PATH (Windows), and the reverse
/// on uninstall. A hook must never fail an install or an uninstall, so every effect catches and logs its own failure.
/// A null PATH action means there is nothing to do on this platform.
/// </summary>
internal sealed class InstallHooks(IAutostart autostart, Func<string> trayExecutable,
    Action<string>? addToPath, Action<string>? removeFromPath, ILog log,
    Func<bool>? choiceMade = null, Action? recordChoice = null, Action? forgetChoice = null)
{
    /// <summary>
    /// The first run of an installed copy, unless a choice was already recorded: Velopack also reports a first
    /// run after a reinstall, which must not undo the user turning it off.
    /// </summary>
    public void OnFirstRun() => Attempt("enable autostart", () =>
    {
        if (choiceMade?.Invoke() == true) return;
        autostart.Enable(trayExecutable());
        recordChoice?.Invoke();
    });

    public void OnInstalled(string cliDir) => Attempt("add to PATH", () => addToPath?.Invoke(cliDir));

    public void OnUninstalling(string cliDir)
    {
        Attempt("disable autostart", autostart.Disable);
        // Otherwise the marker outlives the app and a reinstall would start with autostart off.
        Attempt("forget the autostart choice", () => forgetChoice?.Invoke());
        Attempt("remove from PATH", () => removeFromPath?.Invoke(cliDir));
    }

    private void Attempt(string what, Action effect)
    {
        try { effect(); }
        catch (Exception e) { log.Info($"install hook: could not {what}: {e.Message}"); }
    }
}
