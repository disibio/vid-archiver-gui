using Avalonia;
using VidArchiverGui.App.Services;

namespace VidArchiverGui.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            // Startup failures (e.g. an unreadable settings file) happen before any window exists to report them.
            CrashLog.Write(e);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
