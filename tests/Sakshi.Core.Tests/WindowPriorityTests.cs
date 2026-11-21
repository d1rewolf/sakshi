using Sakshi.Core.Text;

namespace Sakshi.Core.Tests;

public class WindowPriorityTests
{
    private static readonly string[] Windows = ["Slack", "Zoom Meeting", "Mail", "Microsoft Teams"];

    [Fact]
    public void Matching_items_come_first_in_their_original_order()
    {
        Assert.Equal(["Zoom Meeting", "Microsoft Teams", "Slack", "Mail"], WindowPriority.Order(Windows, s => s, "zoom|teams"));
    }

    [Fact]
    public void An_invalid_regex_keeps_the_order()
    {
        Assert.Equal(Windows, WindowPriority.Order(Windows, s => s, "("));
    }

    [Fact]
    public void TryOrder_signals_an_invalid_regex_with_null()
    {
        Assert.Null(WindowPriority.TryOrder(Windows, s => s, "("));
    }
}
