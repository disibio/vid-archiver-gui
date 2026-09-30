using System.Runtime.InteropServices;

namespace VidArchiverGui.Core.Services;

public static class AppPaths
{
    /// <summary>Package family name when running as an MSIX (Microsoft Store) install, otherwise null.</summary>
    /// <remarks>Declared before <see cref="DataDir"/>: static initializers run in order and DataDir depends on it.</remarks>
    public static string? PackageFamilyName { get; } = GetPackageFamilyName();

    public static bool IsPackaged => PackageFamilyName is not null;

    /// <summary>Running as a Flatpak (e.g. from Flathub).</summary>
    public static bool IsFlatpak { get; } = Environment.GetEnvironmentVariable("FLATPAK_ID") is { Length: > 0 };

    /// <summary>
    /// The user's ~/.config (or $XDG_CONFIG_HOME), where Linux browsers keep their profiles. Inside a Flatpak,
    /// XDG_CONFIG_HOME points at the sandbox's own folder instead, so the real one is used.
    /// </summary>
    public static string UserConfigDir
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return !IsFlatpak && Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config");
        }
    }

    /// <summary>
    /// Per-user data folder (settings, managed yt-dlp/ffmpeg). If a file named "portable.txt" sits next to the
    /// executable, data is kept beside the executable instead. Store (MSIX) installs use the package's own folder.
    /// </summary>
    public static string DataDir { get; } = ResolveDataDir();

    /// <summary>
    /// The app's own folder under the user's Videos (Movies on macOS), where the default presets and rules save.
    /// </summary>
    public static string AppVideosFolder
    {
        get
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrEmpty(videos))
            {
                videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
            }

            return Path.Combine(videos, "Vid Archiver GUI");
        }
    }

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

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    public static bool IsUnder(string path, string folder)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return path.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
