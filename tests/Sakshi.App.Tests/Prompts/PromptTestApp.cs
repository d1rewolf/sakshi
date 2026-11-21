using Avalonia;
using Avalonia.Headless;
using Sakshi.App.Prompts;

namespace Sakshi.App.Tests.Prompts;

/// <summary>Headless Avalonia application for the prompt tests: no real window, no desktop session.</summary>
public sealed class PromptTestApp : PromptApplication
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<PromptTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

/// <summary>
/// One headless session for the whole test run. Avalonia.Headless.XUnit 12.1.6 is built against xunit.v3
/// extensibility 3.2.2 and fails discovery under xunit.v3 4.0.x (MissingMethodException), so tests use
/// HeadlessUnitTestSession from plain [Fact]s instead.
/// </summary>
internal static class HeadlessHost
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(PromptTestApp)));

    public static Task Run(Func<Task> test) => Session.Value.Dispatch(async () => { await test(); return 0; }, CancellationToken.None);
}
