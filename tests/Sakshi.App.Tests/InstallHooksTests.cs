using Sakshi.App.Install;
using Sakshi.Core.Ports;
using Sakshi.Platform.Autostart;

namespace Sakshi.App.Tests;

public sealed class InstallHooksTests
{
    private sealed class ListLog : ILog
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) => Lines.Add(message);
    }

    private sealed class FakeAutostart : IAutostart
    {
        public bool IsEnabled { get; private set; }
        public string? Executable { get; private set; }
        public bool Throws { get; init; }
        public void Enable(string executable)
        {
            if (Throws) throw new IOException("disk full");
            IsEnabled = true; Executable = executable;
        }
        public void Disable()
        {
            if (Throws) throw new IOException("disk full");
            IsEnabled = false;
        }
    }

    [Fact]
    public void First_run_turns_autostart_on()
    {
        var autostart = new FakeAutostart();
        new InstallHooks(autostart, () => "/app/sakshiw", null, null, new ListLog()).OnFirstRun();
        Assert.True(autostart.IsEnabled);
        Assert.Equal("/app/sakshiw", autostart.Executable);
    }

    [Fact]
    public void First_run_without_a_recorded_choice_enables_and_records_it()
    {
        var autostart = new FakeAutostart();
        var recorded = 0;
        new InstallHooks(autostart, () => "x", null, null, new ListLog(), () => false, () => recorded++).OnFirstRun();
        Assert.True(autostart.IsEnabled);
        Assert.Equal(1, recorded);
    }

    [Fact]
    public void First_run_after_a_recorded_choice_does_nothing()
    {
        var autostart = new FakeAutostart();
        var recorded = 0;
        new InstallHooks(autostart, () => "x", null, null, new ListLog(), () => true, () => recorded++).OnFirstRun();
        Assert.False(autostart.IsEnabled);
        Assert.Equal(0, recorded);
    }

    [Fact]
    public void Install_adds_the_cli_dir_to_path_and_uninstall_removes_it_and_autostart()
    {
        var autostart = new FakeAutostart();
        autostart.Enable("x");
        var added = new List<string>();
        var removed = new List<string>();
        var hooks = new InstallHooks(autostart, () => "x", added.Add, removed.Add, new ListLog());

        hooks.OnInstalled(@"C:\sakshi\current");
        hooks.OnUninstalling(@"C:\sakshi\current");

        Assert.Equal([@"C:\sakshi\current"], added);
        Assert.Equal([@"C:\sakshi\current"], removed);
        Assert.False(autostart.IsEnabled);
    }

    [Fact]
    public void Without_path_actions_install_and_uninstall_only_touch_autostart()
    {
        var autostart = new FakeAutostart();
        autostart.Enable("x");
        var hooks = new InstallHooks(autostart, () => "x", null, null, new ListLog());
        hooks.OnInstalled("/dir");
        hooks.OnUninstalling("/dir");
        Assert.False(autostart.IsEnabled);
    }

    [Fact]
    public void Uninstall_forgets_the_choice_after_disabling_so_a_reinstall_starts_at_login_again()
    {
        var autostart = new FakeAutostart();
        autostart.Enable("x");
        var order = new List<string>();
        var hooks = new InstallHooks(autostart, () => "x", null, null, new ListLog(),
            forgetChoice: () => order.Add(autostart.IsEnabled ? "forget while enabled" : "forget"));

        hooks.OnUninstalling("/dir");

        Assert.Equal(["forget"], order);
    }

    [Fact]
    public void A_failing_forget_is_logged_and_the_rest_of_uninstall_still_runs()
    {
        var log = new ListLog();
        var removed = new List<string>();
        var hooks = new InstallHooks(new FakeAutostart(), () => "x", null, removed.Add, log,
            forgetChoice: () => throw new IOException("locked"));

        hooks.OnUninstalling("/dir");

        Assert.Contains(log.Lines, l => l.Contains("locked"));
        Assert.Equal(["/dir"], removed);
    }

    [Fact]
    public void Forgetting_deletes_only_the_marker()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sakshi-choice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var choice = new AutostartChoice(dir);
            choice.Forget();   // nothing recorded yet (not even the folder): no error
            choice.Record();
            File.WriteAllText(Path.Combine(dir, "state.json"), "{}");

            choice.Forget();

            Assert.False(choice.Made);
            Assert.True(File.Exists(Path.Combine(dir, "state.json")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_failing_effect_is_logged_not_thrown()
    {
        var log = new ListLog();
        var hooks = new InstallHooks(new FakeAutostart { Throws = true }, () => "x",
            _ => throw new InvalidOperationException("no registry"), _ => throw new IOException("locked"), log);

        hooks.OnFirstRun();
        hooks.OnInstalled("/dir");
        hooks.OnUninstalling("/dir");

        Assert.Equal(4, log.Lines.Count);
        Assert.Contains("disk full", log.Lines[0]);
        Assert.Contains("no registry", log.Lines[1]);
    }
}
