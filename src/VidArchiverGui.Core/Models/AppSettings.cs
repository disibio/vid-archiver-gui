using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VidArchiverGui.Core.Models;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public partial class AppSettings : ObservableObject
{
    [ObservableProperty] private AppTheme _theme = AppTheme.System;

    public ObservableCollection<Preset> Presets { get; set; } = [];
    public ObservableCollection<RoutingRule> Rules { get; set; } = [];

    [ObservableProperty] private string? _defaultPresetId;

    /// <summary>Used when no rule matches. Supports the same tokens as rule destinations.</summary>
    [ObservableProperty] private string _fallbackDestination = "";

    public ObservableCollection<Downloader> Downloaders { get; set; } = [];

    [ObservableProperty] private string _defaultDownloaderId = Downloader.StableId;

    /// <summary>Custom ffmpeg executable or folder. Empty = app-managed copy, then PATH.</summary>
    [ObservableProperty] private string? _ffmpegPath;

    [ObservableProperty] private int _maxConcurrentDownloads = 2;
    [ObservableProperty] private bool _autoStartDownloads;

    /// <summary>Show a system notification when the queue finishes while the window isn't in front.</summary>
    [ObservableProperty] private bool _notifyWhenDone;

    /// <summary>Pause between requests and videos and back off longer on errors (see <see cref="Services.DownloadRunner.GentleArgs"/>).</summary>
    [ObservableProperty] private bool _gentleDownloads;

    /// <summary>Adds --ignore-errors, so a failed extra (e.g. subtitles hitting HTTP 429) is a warning and the video still downloads.</summary>
    [ObservableProperty] private bool _ignoreExtraErrors = true;

    /// <summary>When extraction fails, try the other downloaders of the same kind (installing them if needed).</summary>
    [ObservableProperty] private bool _autoFallback = true;
    [ObservableProperty] private bool _autoUpdateYtDlp = true;
    [ObservableProperty] private DateTimeOffset? _lastYtDlpUpdateCheck;

    /// <summary>
    /// Downloader id → release the user rolled back from. The daily update check skips that release, so a bad version
    /// isn't re-installed the next day; the next release (or a manual update) installs normally.
    /// </summary>
    public Dictionary<string, string> SkippedVersions { get; set; } = [];

    /// <summary>Ask GitHub once a day whether a newer version of the app is out (never on the Store, Flatpak or AUR builds).</summary>
    [ObservableProperty] private bool _checkForAppUpdates = true;
    [ObservableProperty] private DateTimeOffset? _lastAppUpdateCheck;

    /// <summary>The newest release that check found (its tag), so the notice is shown again on every start until acted on.</summary>
    [ObservableProperty] private string? _latestAppVersion;

    /// <summary>An app version the user chose "Skip this version" for; newer ones are still offered.</summary>
    [ObservableProperty] private string? _skippedAppVersion;

    /// <summary>Cookie files and browser specs the user added (installed browsers are detected, not stored).</summary>
    public ObservableCollection<CookieSource> CookieSources { get; set; } = [];

    /// <summary>Cookie choice last picked on the Downloads tab; null = no cookies.</summary>
    [ObservableProperty] private string? _lastCookieId;

    /// <summary>Downloads that were still in the list when the app closed; restored on the next start.</summary>
    public List<SavedDownload> UnfinishedDownloads { get; set; } = [];

    /// <summary>Whether the Downloads tab's log panel is open; open until the user closes it.</summary>
    [ObservableProperty] private bool _showLog = true;

    /// <summary>Height of the log panel's text, as last dragged by the user.</summary>
    [ObservableProperty] private double _logHeight = 180;

    /// <summary>The window's size when it was last closed (not maximized); null until then.</summary>
    [ObservableProperty] private double? _windowWidth;
    [ObservableProperty] private double? _windowHeight;
    [ObservableProperty] private bool _windowMaximized;

    public Preset? FindPreset(string? id) => id is null ? null : Presets.FirstOrDefault(p => p.Id == id);

    [JsonIgnore]
    public Preset DefaultPreset => FindPreset(DefaultPresetId) ?? Presets.First();

    public Downloader? FindDownloader(string? id) => id is null ? null : Downloaders.FirstOrDefault(e => e.Id == id);

    [JsonIgnore]
    public Downloader DefaultDownloader => FindDownloader(DefaultDownloaderId) ?? Downloaders.First();

    public Downloader DownloaderFor(Preset preset) => FindDownloader(preset.DownloaderId) ?? DefaultDownloader;

    /// <summary>
    /// After a preset, downloader or cookie source is removed: choices that pointed at it fall back to the default
    /// (the first preset, stable yt-dlp) or to "keep what was chosen" / "no cookies".
    /// </summary>
    public void RemoveDanglingReferences()
    {
        if (Presets.Count > 0 && FindPreset(DefaultPresetId) is null)
        {
            DefaultPresetId = Presets[0].Id;
        }

        if (FindDownloader(DefaultDownloaderId) is null)
        {
            DefaultDownloaderId = Downloader.StableId;
        }

        foreach (var preset in Presets.Where(p => p.DownloaderId is not null && FindDownloader(p.DownloaderId) is null))
        {
            preset.DownloaderId = null;
        }

        if (!Services.Cookies.Exists(this, LastCookieId))
        {
            LastCookieId = null;
        }

        foreach (var rule in Rules)
        {
            if (rule.PresetId is not null && FindPreset(rule.PresetId) is null)
            {
                rule.PresetId = null;
            }

            if (!Services.Cookies.Exists(this, rule.CookieId))
            {
                rule.CookieId = null;
            }
        }
    }
}
