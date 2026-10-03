using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;

namespace VidArchiverGui.App.Tests;

/// <summary>
/// Runs a test on Avalonia's UI thread, without a window, for view models that post to the dispatcher (the download
/// list does, as yt-dlp's output arrives).
/// </summary>
public static class Headless
{
    private static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(Headless));

    /// <summary>The real app's styles (it opens no window without a desktop lifetime), drawn with Skia so frames can be captured.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>Saves what <paramref name="window"/> shows as a PNG, if VIDARCHIVERGUI_TEST_SCREENSHOTS names a folder (for looking at UI changes).</summary>
    public static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("VIDARCHIVERGUI_TEST_SCREENSHOTS") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
#pragma warning disable CS0618 // as in App.Snapshots: the default PNG encoding is fine
            window.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
        }
    }

    public static Task Run(Func<Task> test) => Session.Dispatch(async () =>
    {
        await test();
        return true;
    }, CancellationToken.None);

    /// <summary>Waits until <paramref name="condition"/> holds, letting the UI thread run meanwhile; fails after <paramref name="seconds"/>.</summary>
    public static Task WaitUntil(Func<bool> condition, string what, int seconds = 30) => WaitUntil(condition, () => what, seconds);

    /// <summary>As above, with a description worked out when it times out, so it can say what state things were in.</summary>
    public static async Task WaitUntil(Func<bool> condition, Func<string> what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting until " + what());
            }

            await Task.Delay(20);
        }
    }
}
