using System.Globalization;
using Sakshi.Core.Ports;
using Sakshi.TestSupport;

namespace Sakshi.Core.Tests;

public sealed class PortsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sakshi-ports-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class Throwing : INotifier
    {
        public void Notify(string headline, string body, Urgency urgency) => throw new IOException("notify-send is gone");
    }

    private sealed class Lines : ILog
    {
        public List<string> All { get; } = [];
        public void Info(string message) => All.Add(message);
    }

    [Fact]
    public void SafeNotifier_SwallowsAndLogsAFailure()
    {
        var log = new Lines();

        new SafeNotifier(new Throwing(), log).Notify("Transcript ready: x", "body", Urgency.Normal);

        var line = Assert.Single(log.All);
        Assert.Contains("Transcript ready: x", line);
        Assert.Contains("notify-send is gone", line);
    }

    [Fact]
    public void SafeNotifier_PassesThrough()
    {
        var inner = new FakeNotifier();

        new SafeNotifier(inner, new Lines()).Notify("h", "b", Urgency.Critical);

        Assert.Equal([(Urgency.Critical, "h", "b")], inner.Sent);
    }

    [Fact]
    public void FileLog_TimestampIsGregorianUnderEveryCulture()
    {
        var path = Path.Combine(_dir, "sub", "sakshi.log");
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH"); // Thai Buddhist calendar: year 2569 for 2026
            new FileLog(path).Info("hello");
        }
        finally { CultureInfo.CurrentCulture = saved; }

        var line = File.ReadAllText(path);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d hello\n$", line);
        Assert.StartsWith(DateTime.Now.Year.ToString(CultureInfo.InvariantCulture), line);
    }
}
