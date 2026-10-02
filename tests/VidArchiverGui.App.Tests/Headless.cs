using Avalonia;
using Avalonia.Headless;

namespace VidArchiverGui.App.Tests;

/// <summary>
/// Runs a test on Avalonia's UI thread, without a window, for view models that post to the dispatcher (the download
/// list does, as yt-dlp's output arrives).
/// </summary>
public static class Headless
{
    private static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(Headless));

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

    public static Task Run(Func<Task> test) => Session.Dispatch(async () =>
    {
        await test();
        return true;
    }, CancellationToken.None);

    /// <summary>Waits until <paramref name="condition"/> holds, letting the UI thread run meanwhile; fails after <paramref name="seconds"/>.</summary>
    public static async Task WaitUntil(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting until " + what);
            }

            await Task.Delay(20);
        }
    }
}
