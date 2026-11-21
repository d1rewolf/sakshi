using Sakshi.Core.HandOff;
using Sakshi.Core.Ports;
using Sakshi.TestSupport;

namespace Sakshi.Core.Tests;

[Collection("Environment")]
public sealed class HandOffTests : IDisposable
{
    private sealed class Recorder : INotifier
    {
        public List<(string Headline, string Body, Urgency Urgency)> Sent { get; } = [];
        public void Notify(string headline, string body, Urgency urgency) => Sent.Add((headline, body, urgency));
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sakshi-handoff-" + Guid.NewGuid().ToString("N"));
    private readonly string _dir;
    private readonly string _argsFile;
    private readonly string _bin;
    private readonly Recorder _notifier = new();
    private readonly string?[] _saved;
    private static readonly string[] Vars = ["TEST_ARGS_FILE", "TEST_EXIT_CODE", "TEST_SPAWN_SLEEPER", "TEST_READ_STDIN", "TEST_SLEEP_SECONDS"];

    public HandOffTests()
    {
        _dir = Path.Combine(_root, "meetings", "Weekly_Sync");
        Directory.CreateDirectory(_dir);
        _argsFile = Path.Combine(_root, "args.txt");
        _bin = TestHelper.Install(_root);
        _saved = Vars.Select(Environment.GetEnvironmentVariable).ToArray();
        Environment.SetEnvironmentVariable("TEST_ARGS_FILE", _argsFile);
        foreach (var v in Vars.Skip(1)) Environment.SetEnvironmentVariable(v, null);
    }

    public void Dispose()
    {
        for (var i = 0; i < Vars.Length; i++) Environment.SetEnvironmentVariable(Vars[i], _saved[i]);
        // Windows cannot delete the exe of a still-running detached helper: retry briefly, then give up.
        for (var i = 0; i < 8; i++)
        {
            try { Directory.Delete(_root, recursive: true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(500); }
        }
    }

    private HandOffRunner Runner(string pathVar = "", params string[] extra) =>
        new(new CommandResolver(extra, n => n == "PATH" ? pathVar : Environment.GetEnvironmentVariable(n)), _notifier);

    private string Log => File.ReadAllText(Path.Combine(_dir, "handoff.log"));

    [Fact]
    public async Task Runs_with_its_own_words_then_the_meeting_folder()
    {
        var ok = await Runner(extra: _bin).RunAsync($"{TestHelper.Name} requirements new", _dir, "Weekly_Sync", CancellationToken.None);
        Assert.True(ok);
        Assert.Equal(["requirements", "new", _dir], File.ReadAllLines(_argsFile));
        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public async Task Handoff_log_has_the_command_line_and_both_streams()
    {
        await Runner(extra: _bin).RunAsync($"{TestHelper.Name} requirements new", _dir, "Weekly_Sync", CancellationToken.None);
        var lines = Log.Split('\n');
        Assert.Equal($"$ {TestHelper.Name} requirements new {_dir}", lines[0]);
        Assert.Contains("helper stdout", Log);
        Assert.Contains("helper stderr", Log);
        Assert.DoesNotContain("exit status", Log);
    }

    [Fact]
    public async Task Failing_hand_off_notifies_critically_and_logs_the_status()
    {
        Environment.SetEnvironmentVariable("TEST_EXIT_CODE", "3");
        var video = Path.Combine(_dir, "failing.mp4");
        File.WriteAllText(video, "v");
        var ok = await Runner(extra: _bin).RunAsync($"{TestHelper.Name} requirements new", _dir, "failing", CancellationToken.None);
        Assert.False(ok);
        var n = Assert.Single(_notifier.Sent);
        Assert.Equal("Hand-off failed: failing", n.Headline);
        Assert.Equal($"\"{TestHelper.Name} requirements new\" exited with status 3. The meeting is untouched in {_dir}; see handoff.log", n.Body);
        Assert.Equal(Urgency.Critical, n.Urgency);
        Assert.Contains("exit status 3\n", Log);
        Assert.True(File.Exists(video));
        Assert.Equal(["failing.mp4", "handoff.log"], Directory.GetFiles(_dir).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public async Task Missing_command_fails_the_same_way_with_status_127()
    {
        var ok = await Runner().RunAsync("no-such-handoff-tool x", _dir, "missing", CancellationToken.None);
        Assert.False(ok);
        var n = Assert.Single(_notifier.Sent);
        Assert.Equal("Hand-off failed: missing", n.Headline);
        Assert.Equal($"\"no-such-handoff-tool x\" exited with status 127. The meeting is untouched in {_dir}; see handoff.log", n.Body);
        Assert.Equal(Urgency.Critical, n.Urgency);
        Assert.StartsWith($"$ no-such-handoff-tool x {_dir}\n", Log);
        Assert.Contains("exit status 127\n", Log);
    }

    [Fact]
    public async Task Blank_hand_off_does_nothing()
    {
        var ok = await Runner().RunAsync("   ", _dir, "plain", CancellationToken.None);
        Assert.True(ok);
        Assert.False(File.Exists(Path.Combine(_dir, "handoff.log")));
        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public async Task Bare_command_is_found_on_path()
    {
        var ok = await Runner(pathVar: _bin).RunAsync(TestHelper.Name, _dir, "n", CancellationToken.None);
        Assert.True(ok);
        Assert.Equal([_dir], File.ReadAllLines(_argsFile));
    }

    [Fact]
    public async Task Command_with_a_separator_is_used_as_is()
    {
        // _root is a GUID dir under the temp path; skip if the temp path has a space (words split on whitespace).
        Assert.SkipWhen(_bin.Contains(' '), "temp path contains a space");
        var full = Path.Combine(_bin, TestHelper.Name + (OperatingSystem.IsWindows() ? ".exe" : ""));
        var ok = await Runner().RunAsync($"{full} a", _dir, "n", CancellationToken.None);
        Assert.True(ok);
        Assert.Equal(["a", _dir], File.ReadAllLines(_argsFile));
    }

    [Fact]
    public void Resolver_prefers_path_over_extra_dirs_and_returns_null_when_absent()
    {
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        TestHelper.Install(Path.Combine(_root, "second")); // second/bin
        var second = Path.Combine(_root, "second", "bin");
        var r = new CommandResolver([second], n => n == "PATH" ? _bin : Environment.GetEnvironmentVariable(n));
        Assert.StartsWith(_bin, r.Resolve(TestHelper.Name));
        var extraOnly = new CommandResolver([second], n => n == "PATH" ? other : Environment.GetEnvironmentVariable(n));
        Assert.StartsWith(second, extraOnly.Resolve(TestHelper.Name));
        Assert.Null(extraOnly.Resolve("nope-nope"));
        Assert.Equal(Path.Combine("a", "b"), extraOnly.Resolve(Path.Combine("a", "b")));
    }

    [Fact]
    public async Task Failed_start_logs_the_reason_before_the_status()
    {
        await Runner().RunAsync("no-such-handoff-tool", _dir, "n", CancellationToken.None);
        var lines = Log.TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("exit status 127", lines[^1]);
        Assert.NotEmpty(lines[1]);
    }

    [Fact]
    public async Task A_literal_star_argument_reaches_the_command_unexpanded()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "x");
        await Runner(extra: _bin).RunAsync($"{TestHelper.Name} * ?", _dir, "n", CancellationToken.None);
        Assert.Equal(["*", "?", _dir], File.ReadAllLines(_argsFile));
    }

    [Fact]
    public async Task Stdin_is_closed()
    {
        Environment.SetEnvironmentVariable("TEST_READ_STDIN", "1");
        var run = Runner(extra: _bin).RunAsync(TestHelper.Name, _dir, "n", CancellationToken.None);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)));
        Assert.True(await run);
    }

    [Fact]
    public async Task A_background_child_holding_the_pipes_does_not_hang_the_runner()
    {
        Environment.SetEnvironmentVariable("TEST_SPAWN_SLEEPER", "1");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ok = await Runner(extra: _bin).RunAsync(TestHelper.Name, _dir, "n", CancellationToken.None);
        watch.Stop();
        Assert.True(ok);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.Contains("(output after exit not captured)", Log);
    }

    [Fact]
    public async Task Extra_dir_fallback_works_through_the_runner()
    {
        var other = Path.Combine(_root, "empty");
        Directory.CreateDirectory(other);
        var ok = await Runner(pathVar: other, _bin).RunAsync(TestHelper.Name, _dir, "n", CancellationToken.None);
        Assert.True(ok);
    }

    [Fact]
    public void Non_executable_file_on_path_is_skipped()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix execute bits"); return; }
        var dud = Path.Combine(_root, "dud");
        Directory.CreateDirectory(dud);
        var file = Path.Combine(dud, TestHelper.Name);
        File.WriteAllText(file, "not a program");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var r = new CommandResolver([], n => n == "PATH" ? dud + Path.PathSeparator + _bin : null);
        Assert.StartsWith(_bin, r.Resolve(TestHelper.Name));
    }

    [Fact]
    public void Cmd_line_quotes_every_token()
    {
        var line = CmdExeCommandLine.Build(@"C:\Users\Jane Doe\bin\file-meeting.cmd", ["a b", @"C:\m&m\dir"], out var refusal);
        Assert.Null(refusal);
        Assert.Equal("/d /s /c \"\"C:\\Users\\Jane Doe\\bin\\file-meeting.cmd\" \"a b\" \"C:\\m&m\\dir\"\"", line);
    }

    [Theory]
    [InlineData("a\"b")]
    [InlineData("100%")]
    [InlineData("a^b")]
    [InlineData("a\rb")]
    [InlineData("a\nb")]
    public void Cmd_line_refuses_characters_cmd_would_interpret(string token)
    {
        Assert.Null(CmdExeCommandLine.Build("p.cmd", [token], out var refusal));
        Assert.StartsWith("refusing to run through cmd.exe", refusal);
        Assert.Null(CmdExeCommandLine.Build(token, [], out _));
    }

    [Fact]
    public async Task Windows_runs_a_cmd_in_a_spaced_dir_with_an_ampersand_in_the_meeting_dir()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe only");
        var scriptDir = Path.Combine(_root, "sp ace");
        Directory.CreateDirectory(scriptDir);
        var script = Path.Combine(scriptDir, "handoff-test.cmd");
        File.WriteAllText(script, "@echo off\r\necho \"%~1\">>\"%TEST_ARGS_FILE%\"\r\necho \"%~2\">>\"%TEST_ARGS_FILE%\"\r\n");
        var meeting = Path.Combine(_root, "m&m");
        Directory.CreateDirectory(meeting);
        var ok = await Runner(extra: scriptDir).RunAsync("handoff-test arg", meeting, "n", CancellationToken.None);
        Assert.True(ok);
        Assert.Equal(["arg", meeting], File.ReadAllLines(_argsFile).Select(l => l.Trim().Trim('"')).ToArray());
    }
}
