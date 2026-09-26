using System.Runtime.InteropServices;

namespace VidArchiverGui.Core.Services;

public static class AppPaths
{
    /// <summary>Package family name when running as an MSIX (Microsoft Store) install, otherwise null.</summary>
    /// <remarks>Declared before <see cref="DataDir"/>: static initializers run in order and DataDir depends on it.</remarks>
    public static string? PackageFamilyName { get; } = GetPackageFamilyName();

    public static bool IsPackaged => PackageFamilyName is not null;

    /// <summary>
    /// Per-user data folder (settings, managed yt-dlp/ffmpeg). If a file named "portable.txt" sits next to the
    /// executable, data is kept beside the executable instead. Store (MSIX) installs use the package's own folder.
    /// </summary>
    public static string DataDir { get; } = ResolveDataDir();

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    public static string BinDir
    {
        get
        {
            var dir = Path.Combine(DataDir, "bin");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string ExeName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static string ResolveDataDir()
    {
        // MSIX redirects writes under %APPDATA% into the package, where Explorer ("Open data folder") can't see them.
        // Use the package's LocalState directly instead; Windows removes it when the app is uninstalled.
        if (PackageFamilyName is not null)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var state = Path.Combine(local, "Packages", PackageFamilyName, "LocalState");
            Directory.CreateDirectory(state);
            return state;
        }

        var baseDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, "portable.txt")))
        {
            var portable = Path.Combine(baseDir, "data");
            Directory.CreateDirectory(portable);
            return portable;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        var dir = Path.Combine(appData, "VidArchiverGui");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string? GetPackageFamilyName()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8))
        {
            return null;
        }
        uint length = 0;
        if (GetCurrentPackageFamilyName(ref length, null) != ErrorInsufficientBuffer)
        {
            return null; // APPMODEL_ERROR_NO_PACKAGE: not running from a package
        }
        var buffer = new char[length];
        return GetCurrentPackageFamilyName(ref length, buffer) == 0 ? new string(buffer, 0, (int)length - 1) : null;
    }

    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, [Out] char[]? packageFamilyName);
}
