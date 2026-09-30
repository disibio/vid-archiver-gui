using System.Diagnostics.CodeAnalysis;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

/// <summary>
/// Turns a cookie choice (none, a detected browser, or a user-added source) into downloader arguments.
/// Cookies are always opt-in per download: they tie the download to the user's account.
/// </summary>
public static class Cookies
{
    public const string NoneId = "none";
    private const string BrowserPrefix = "browser:";

    /// <summary>Browsers yt-dlp can read cookies from, in the order they are offered.</summary>
    private static readonly (string Key, string Name)[] Browsers =
    [
        ("firefox", "Firefox"), ("waterfox", "Waterfox"), ("librewolf", "LibreWolf"), ("floorp", "Floorp"), ("zen", "Zen"),
        ("chrome", "Chrome"), ("edge", "Edge"), ("brave", "Brave"), ("chromium", "Chromium"),
        ("vivaldi", "Vivaldi"), ("opera", "Opera"), ("whale", "Whale"), ("safari", "Safari"),
    ];

    /// <summary>
    /// Firefox-based browsers yt-dlp doesn't know by name. It reads them as Firefox when given their profile folder,
    /// picking the most recently used profile in it.
    /// </summary>
    private static readonly HashSet<string> FirefoxBased = ["waterfox", "librewolf", "floorp", "zen"];

    public static string BrowserId(string key) => BrowserPrefix + key;

    /// <summary>Whether <paramref name="id"/> is a detected browser rather than a user-added source.</summary>
    public static bool IsBrowser([NotNullWhen(true)] string? id) => id?.StartsWith(BrowserPrefix, StringComparison.Ordinal) == true;

    /// <summary>Browsers whose profile folder exists on this computer.</summary>
    public static IReadOnlyList<Choice> DetectBrowsers() =>
        Browsers.Where(b => ProfileDirs(b.Key).Any(Directory.Exists)).Select(b => new Choice(BrowserId(b.Key), b.Name)).ToList();

    /// <summary>Where each browser keeps its profiles (the places yt-dlp looks by default).</summary>
    internal static IEnumerable<string> ProfileDirs(string browser)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return browser switch
            {
                "firefox" => [Path.Combine(roaming, "Mozilla", "Firefox", "Profiles")],
                "waterfox" => [Path.Combine(roaming, "Waterfox", "Profiles")],
                "librewolf" => [Path.Combine(roaming, "librewolf", "Profiles")],
                "floorp" => [Path.Combine(roaming, "Floorp", "Profiles")],
                "zen" => [Path.Combine(roaming, "zen", "Profiles")],
                "chrome" => [Path.Combine(local, "Google", "Chrome", "User Data")],
                "edge" => [Path.Combine(local, "Microsoft", "Edge", "User Data")],
                "brave" => [Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")],
                "chromium" => [Path.Combine(local, "Chromium", "User Data")],
                "vivaldi" => [Path.Combine(local, "Vivaldi", "User Data")],
                "opera" => [Path.Combine(roaming, "Opera Software", "Opera Stable")],
                "whale" => [Path.Combine(local, "Naver", "Naver Whale", "User Data")],
                _ => [],
            };
        }
        if (OperatingSystem.IsMacOS())
        {
            var support = Path.Combine(home, "Library", "Application Support");
            return browser switch
            {
                "firefox" => [Path.Combine(support, "Firefox", "Profiles")],
                "waterfox" => [Path.Combine(support, "Waterfox", "Profiles")],
                "librewolf" => [Path.Combine(support, "librewolf", "Profiles")],
                "floorp" => [Path.Combine(support, "Floorp", "Profiles")],
                "zen" => [Path.Combine(support, "zen", "Profiles")],
                "chrome" => [Path.Combine(support, "Google", "Chrome")],
                "edge" => [Path.Combine(support, "Microsoft Edge")],
                "brave" => [Path.Combine(support, "BraveSoftware", "Brave-Browser")],
                "chromium" => [Path.Combine(support, "Chromium")],
                "vivaldi" => [Path.Combine(support, "Vivaldi")],
                "opera" => [Path.Combine(support, "com.operasoftware.Opera")],
                "whale" => [Path.Combine(support, "Naver", "Whale")],
                "safari" => [Path.Combine(home, "Library", "Containers", "com.apple.Safari"), Path.Combine(home, "Library", "Cookies")],
                _ => [],
            };
        }
        var config = AppPaths.UserConfigDir;
        return browser switch
        {
            "firefox" => [Path.Combine(home, ".mozilla", "firefox"), Path.Combine(home, "snap", "firefox", "common", ".mozilla", "firefox"),
                          Path.Combine(home, ".var", "app", "org.mozilla.firefox", ".mozilla", "firefox")],
            // Their own installs, then their Flathub builds.
            "waterfox" => [Path.Combine(home, ".waterfox"), Path.Combine(home, ".var", "app", "net.waterfox.waterfox", ".waterfox")],
            "librewolf" => [Path.Combine(home, ".librewolf"), Path.Combine(home, ".var", "app", "io.gitlab.librewolf-community", ".librewolf")],
            "floorp" => [Path.Combine(home, ".floorp"), Path.Combine(home, ".var", "app", "one.ablaze.floorp", ".floorp")],
            "zen" => [Path.Combine(home, ".zen"), Path.Combine(home, ".var", "app", "app.zen_browser.zen", ".zen")],
            "chrome" => [Path.Combine(config, "google-chrome")],
            "edge" => [Path.Combine(config, "microsoft-edge")],
            "brave" => [Path.Combine(config, "BraveSoftware", "Brave-Browser")],
            "chromium" => [Path.Combine(config, "chromium"), Path.Combine(home, "snap", "chromium", "common", "chromium")],
            "vivaldi" => [Path.Combine(config, "vivaldi")],
            "opera" => [Path.Combine(config, "opera")],
            "whale" => [Path.Combine(config, "naver-whale")],
            _ => [],
        };
    }

    /// <summary>
    /// "No cookies", then detected browsers, then the user's own sources. Browsers in <paramref name="inUse"/> that
    /// weren't found (e.g. picked by rules imported from another computer) come last, so a drop-down can still show
    /// them rather than replacing them with another choice.
    /// </summary>
    public static List<Choice> Choices(AppSettings settings, IReadOnlyList<Choice> browsers, IEnumerable<string?>? inUse = null)
    {
        List<Choice> choices = [new(NoneId, "No cookies"), .. browsers];
        choices.AddRange(settings.CookieSources.Select(c => new Choice(c.Id, c.Name)));
        choices.AddRange((inUse ?? []).Where(IsBrowser).Distinct().Where(id => browsers.All(b => b.Id != id))
            .Select(id => new Choice(id, DisplayName(settings, id) + " (not found on this computer)")));
        return choices;
    }

    public static string DisplayName(AppSettings settings, string? id)
    {
        if (IsNone(id))
        {
            return "No cookies";
        }

        if (IsBrowser(id))
        {
            return BrowserName(id[BrowserPrefix.Length..]);
        }
        return settings.CookieSources.FirstOrDefault(c => c.Id == id)?.Name ?? "Missing cookie source";
    }

    private static string BrowserName(string key) => Browsers.FirstOrDefault(b => b.Key == key).Name ?? key;

    public static bool IsNone(string? id) => id is null or NoneId;

    /// <summary>False for a user source that has since been removed.</summary>
    public static bool Exists(AppSettings settings, string? id) =>
        IsNone(id) || IsBrowser(id) || settings.CookieSources.Any(c => c.Id == id);

    /// <summary>Downloader arguments for a cookie choice. Throws <see cref="DownloaderException"/> with a readable reason if it can't be used.</summary>
    public static IReadOnlyList<string> Args(AppSettings settings, string? id, DownloaderFlavor flavor)
    {
        if (IsNone(id))
        {
            return [];
        }

        string spec;
        if (IsBrowser(id))
        {
            spec = BrowserSpec(id[BrowserPrefix.Length..], Directory.Exists);
        }
        else
        {
            var source = settings.CookieSources.FirstOrDefault(c => c.Id == id)
                ?? throw new DownloaderException("The selected cookie source was removed. Pick another one in the Cookies list.");
            var value = source.Value.Trim().Trim('"');
            if (value.Length == 0)
            {
                throw new DownloaderException($"Cookie source \"{source.Name}\" is empty. Set it up on the Settings tab.");
            }

            if (source.Kind == CookieSourceKind.File)
            {
                if (!File.Exists(value))
                {
                    throw new DownloaderException($"Cookie file for \"{source.Name}\" not found: {value}");
                }

                return ["--cookies", value];
            }
            spec = value;
        }

        if (flavor == DownloaderFlavor.YoutubeDl)
        {
            throw new DownloaderException("youtube-dl can't read cookies from a browser. Use a cookies.txt file instead (Settings → Cookies).");
        }

        return ["--cookies-from-browser", spec];
    }

    /// <summary>
    /// What --cookies-from-browser gets for a detected browser: its name, or for a <see cref="FirefoxBased"/> one
    /// "firefox:" and the profile folder that <paramref name="dirExists"/> finds.
    /// </summary>
    internal static string BrowserSpec(string key, Func<string, bool> dirExists)
    {
        if (!FirefoxBased.Contains(key))
        {
            return key;
        }

        return ProfileDirs(key).FirstOrDefault(dirExists) is { } dir
            ? "firefox:" + dir
            : throw new DownloaderException($"{BrowserName(key)} wasn't found on this computer. Pick other cookies in the Cookies list.");
    }
}
