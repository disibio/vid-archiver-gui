using Avalonia;
using VidArchiverGui.Core.Services;

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
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.AppendAllText(Path.Combine(AppPaths.DataDir, "crash.log"), $"[{DateTime.Now:O}] {e}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (Exception) { }
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
