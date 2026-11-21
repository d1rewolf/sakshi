// Stand-in for a hand-off command. Appends its argv (one per line) to the file
// named by TEST_ARGS_FILE, prints one line to each stream and exits with
// TEST_EXIT_CODE (default 0). Switches:
//   TEST_SLEEP_SECONDS=n   print nothing, sleep n seconds, exit
//   TEST_SPAWN_SLEEPER=1   start a detached copy of itself (sleeping 4 s,
//                          inheriting stdout/stderr), then carry on
//   --log FILE (anywhere in argv) sends the helper's own stdout/stderr to FILE (overwritten),
//                          like `sakshi transcribe --log`; argv is still recorded in full.
//   TEST_PID_FILE=file     write the helper's own pid to file first
//   TEST_DUMP_ENV=NAME     with TEST_ENV_FILE=file: write the value of env var NAME to file (empty when unset)
//   TEST_READ_STDIN=1      read stdin to EOF, then exit (hangs if stdin is open)
using System.Diagnostics;
using System.Text;

var logAt = Array.IndexOf(args, "--log");
if (logAt >= 0 && logAt + 1 < args.Length)
{
    var logWriter = new StreamWriter(new FileStream(args[logAt + 1], FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
    Console.SetOut(logWriter);
    Console.SetError(logWriter);
}

var pidFile = Environment.GetEnvironmentVariable("TEST_PID_FILE");
if (!string.IsNullOrEmpty(pidFile))
    File.WriteAllText(pidFile, Environment.ProcessId.ToString());

if (int.TryParse(Environment.GetEnvironmentVariable("TEST_SLEEP_SECONDS"), out var sleep))
{
    Thread.Sleep(TimeSpan.FromSeconds(sleep));
    return 0;
}
if (Environment.GetEnvironmentVariable("TEST_SPAWN_SLEEPER") == "1")
{
    var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    psi.Environment["TEST_SPAWN_SLEEPER"] = "0";
    psi.Environment["TEST_SLEEP_SECONDS"] = "4";
    Process.Start(psi);
}
if (Environment.GetEnvironmentVariable("TEST_READ_STDIN") == "1")
    Console.In.ReadToEnd();

var dumpEnv = Environment.GetEnvironmentVariable("TEST_DUMP_ENV");
var envFile = Environment.GetEnvironmentVariable("TEST_ENV_FILE");
if (!string.IsNullOrEmpty(dumpEnv) && !string.IsNullOrEmpty(envFile))
    File.WriteAllText(envFile, Environment.GetEnvironmentVariable(dumpEnv) ?? "");

var argsFile = Environment.GetEnvironmentVariable("TEST_ARGS_FILE");
if (!string.IsNullOrEmpty(argsFile))
    File.AppendAllLines(argsFile, args);
Console.Out.WriteLine("helper stdout");
Console.Out.Flush();
Console.Error.WriteLine("helper stderr");
Console.Error.Flush();
return int.TryParse(Environment.GetEnvironmentVariable("TEST_EXIT_CODE"), out var code) ? code : 0;
