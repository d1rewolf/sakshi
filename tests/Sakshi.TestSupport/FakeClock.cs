using Sakshi.Core.Ports;

namespace Sakshi.TestSupport;

public sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; set; } = now;
}
