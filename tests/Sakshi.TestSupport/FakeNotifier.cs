using Sakshi.Core.Ports;

namespace Sakshi.TestSupport;

public sealed class FakeNotifier : INotifier
{
    public List<(Urgency Urgency, string Headline, string Body)> Sent { get; } = [];

    public void Notify(string headline, string body, Urgency urgency)
    {
        lock (Sent) Sent.Add((urgency, headline, body));
    }
}
