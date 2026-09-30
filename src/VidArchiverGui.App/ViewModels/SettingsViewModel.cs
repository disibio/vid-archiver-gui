using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VidArchiverGui.App.Services;
using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.ViewModels;

public partial class DownloaderRowViewModel(Downloader downloader) : ObservableObject
{
    public Downloader Downloader => downloader;
    public string Source => downloader.IsManaged ? "Installed from github.com/" + downloader.GitHubRepo : "Custom executable";
    public string FlavorText => downloader.Flavor == DownloaderFlavor.YtDlp ? "yt-dlp compatible" : "youtube-dl compatible";

    [ObservableProperty] private string _status = "Checking…";
    [ObservableProperty] private bool _isDefault;
    [ObservableProperty] private bool _isAvailable;

    /// <summary>Version kept from before the last update, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRollback), nameof(RollbackText))]
    private string? _previousVersion;

    public bool CanRollback => PreviousVersion is not null;
    public string RollbackText => "Roll back to " + PreviousVersion;
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppHost _host;

    // Refreshes can be triggered back to back (e.g. several installs from "Fix now"); run them one at a time.
    private readonly SemaphoreSlim _refreshGate = new(1);

    public SettingsViewModel(AppHost host)
    {
        _host = host;
        Setup = new SetupViewModel(host, RefreshAsync);
    }

    public SetupViewModel Setup { get; }
    public AppSettings Settings => _host.Settings;
    public string DataFolder => AppPaths.DataDir;
    public string DataFolderHint => AppPaths.IsPackaged
        ? "Settings, rules and presets live in settings.json here. Windows removes this folder when the app is uninstalled."
        : "Settings, rules and presets live in settings.json here. Put a file named portable.txt next to the app to keep data beside it instead.";
    public bool CanDownloadFfmpeg => ToolManager.CanDownloadFfmpeg;
    public string FfmpegHint => ToolManager.FfmpegInstallHint;
    public static DownloaderFlavor[] Flavors { get; } = Enum.GetValues<DownloaderFlavor>();
    public static AppTheme[] Themes { get; } = Enum.GetValues<AppTheme>();
    public static CookieSourceKind[] CookieKinds { get; } = Enum.GetValues<CookieSourceKind>();

    public ObservableCollection<DownloaderRowViewModel> Downloaders { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanRemoveSelected), nameof(InstallButtonText))]
    private DownloaderRowViewModel? _selectedDownloader;

    [ObservableProperty] private string _newDownloaderName = "";
    [ObservableProperty] private string _newDownloaderPath = "";
    [ObservableProperty] private DownloaderFlavor _newDownloaderFlavor = DownloaderFlavor.YtDlp;

    [ObservableProperty] private CookieSource? _selectedCookieSource;
    [ObservableProperty] private string _newCookieName = "";
    [ObservableProperty] private string _newCookieValue = "";
    [ObservableProperty] private CookieSourceKind _newCookieKind = CookieSourceKind.File;
    [ObservableProperty] private string _detectedBrowsers = "";
    [ObservableProperty] private string _cookieMessage = "";

    [ObservableProperty] private string _ffmpegStatus = "Checking…";
    [ObservableProperty] private string _toolOutput = "";

    /// <summary>What the current install/update is doing, with a Cancel button.</summary>
    public BusyStatusViewModel Busy { get; } = new();

    public bool HasSelection => SelectedDownloader is not null;
    public bool CanRemoveSelected => SelectedDownloader is { Downloader.IsManaged: false };
    public string InstallButtonText => SelectedDownloader?.Downloader switch
    {
        { IsManaged: false } => "Update (-U)",
        { } e when ToolManager.IsInstalledByApp(e) => "Check for update",
        _ => "Install",
    };

    /// <summary>First-run install, the daily update check, then the setup checklist.</summary>
    public async Task InitializeAsync()
    {
        Setup.ShowWaiting(VersionCheckText());
        await RefreshToolsAsync();

        // Prefer our own copy over whatever is on PATH (e.g. an outdated pip install) so it can be kept current.
        var downloader = Settings.DefaultDownloader;
        if (downloader.IsManaged && !ToolManager.IsInstalledByApp(downloader))
        {
            await InstallOrUpdate(downloader, auto: false);
        }
        else if (Settings.AutoUpdateYtDlp
                 && (Settings.LastYtDlpUpdateCheck is null || DateTimeOffset.Now - Settings.LastYtDlpUpdateCheck > TimeSpan.FromHours(24)))
        {
            foreach (var e in Settings.Downloaders.Where(ToolManager.IsInstalledByApp).ToList())
            {
                await InstallOrUpdate(e, auto: true);
            }

            if (ToolManager.IsDenoInstalledByApp)
            {
                await UpdateDenoIfNewer();
            }

            Settings.LastYtDlpUpdateCheck = DateTimeOffset.Now;
        }

        await RefreshAsync();
    }

    private async Task UpdateDenoIfNewer()
    {
        await RunBusy("Checking for deno updates", async (progress, ct) =>
        {
            var current = await ToolManager.GetDenoVersionAsync(ToolManager.ManagedDenoPath, ct);
            var latest = await ToolManager.GetLatestDenoVersionAsync(ct);
            if (!ToolManager.IsNewer(latest, current))
            {
                return $"deno is up to date ({current}).";
            }

            await ToolManager.InstallDenoAsync(progress, ct);
            return $"deno updated {current} → {latest}.";
        }, quietOnError: true);
    }

    /// <summary>Refreshes the tool list and re-runs the setup check.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        Setup.ShowWaiting(VersionCheckText());
        await RefreshToolsAsync();
        await Setup.CheckAsync();
    }

    /// <summary>What the checklist says while RefreshToolsAsync asks each installed downloader for its version.</summary>
    private string VersionCheckText()
    {
        var installed = Settings.Downloaders.Where(e => ToolManager.LocatePath(e) is not null).Select(e => e.Name).ToList();
        return installed switch
        {
            [] => "looking for installed downloaders…",
            [var one] => $"checking which version of {one} is installed…",
            _ => $"checking which versions of {string.Join(", ", installed[..^1])} and {installed[^1]} are installed…",
        };
    }

    private async Task RefreshToolsAsync()
    {
        await _refreshGate.WaitAsync();
        try
        {
            await RefreshToolsCoreAsync();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshToolsCoreAsync()
    {
        var browsers = Cookies.DetectBrowsers();
        DetectedBrowsers = browsers.Count == 0
            ? "No supported browsers found. Add a cookies.txt file below."
            : "Found: " + string.Join(", ", browsers.Select(b => b.Name)) + ". These appear in the Cookies lists automatically.";

        var selectedId = SelectedDownloader?.Downloader.Id;
        var rows = Settings.Downloaders.Select(e => new DownloaderRowViewModel(e) { IsDefault = e.Id == Settings.DefaultDownloaderId }).ToList();
        Downloaders.Clear();
        foreach (var row in rows)
        {
            Downloaders.Add(row);
        }

        SelectedDownloader = Downloaders.FirstOrDefault(r => r.Downloader.Id == selectedId) ?? Downloaders.FirstOrDefault(r => r.IsDefault);

        // All at once: each "--version" can take several seconds (see ToolManager.GetVersionAsync).
        await Task.WhenAll(rows.Select(async row =>
        {
            var path = ToolManager.LocatePath(row.Downloader);
            row.IsAvailable = path is not null;
            if (path is null)
            {
                row.Status = row.Downloader.IsManaged ? "Not installed" : "Executable not found";
                return;
            }

            // Shown while the (possibly slow) version check on the next line runs.
            row.Status = $"Running \"{Path.GetFileName(path)} --version\"…  —  {path}";
            row.Status = $"{await ToolManager.GetVersionAsync(path) ?? "version unknown (--version failed or didn't answer)"}  —  {path}";
            row.PreviousVersion = ToolManager.CanRollback(row.Downloader)
                ? await ToolManager.GetVersionAsync(ToolManager.PreviousPath(row.Downloader)) ?? "previous version"
                : null;
        }));

        var ff = ToolManager.ResolveFfmpeg(Settings.FfmpegPath);
        FfmpegStatus = ff is null
            ? "Not found — merging video+audio and embedding thumbnails/subtitles need ffmpeg."
            : $"{await ToolManager.GetFfmpegVersionAsync(Settings.FfmpegPath) ?? "version unknown"}  —  {ff}";
    }

    [RelayCommand]
    private async Task InstallSelected()
    {
        if (SelectedDownloader is { } row)
        {
            await InstallOrUpdate(row.Downloader, auto: false);
            await RefreshAsync();
        }
    }

    private async Task InstallOrUpdate(Downloader downloader, bool auto)
    {
        if (!downloader.IsManaged)
        {
            if (ToolManager.LocatePath(downloader) is { } exe)
            {
                await RunBusy($"Running {downloader.Name} -U", async (_, ct) => await ToolManager.SelfUpdateAsync(exe, ct));
            }

            return;
        }

        if (!ToolManager.IsInstalledByApp(downloader))
        {
            await RunBusy($"Installing {downloader.Name}", async (progress, ct) =>
            {
                await ToolManager.InstallAsync(downloader, progress, ct);
                return $"{downloader.Name} installed to {ToolManager.ManagedPath(downloader)}";
            });
            return;
        }

        await RunBusy($"Checking for {downloader.Name} updates", async (progress, ct) =>
        {
            var current = await ToolManager.GetVersionAsync(ToolManager.ManagedPath(downloader), ct);
            var latest = await ToolManager.GetLatestVersionAsync(downloader, ct);
            if (!ToolManager.IsNewer(latest, current))
            {
                return $"{downloader.Name} is up to date ({current}).";
            }

            if (auto && latest is not null && Settings.SkippedVersions.GetValueOrDefault(downloader.Id) == latest)
            {
                return $"{downloader.Name} {latest} was rolled back, so it isn't re-installed automatically. Use \"Check for update\" to install it anyway.";
            }

            await ToolManager.InstallAsync(downloader, progress, ct);
            Settings.SkippedVersions.Remove(downloader.Id);

            return $"{downloader.Name} updated {current} → {latest}.";
        }, quietOnError: auto);
    }

    [RelayCommand]
    private async Task RollbackSelected()
    {
        if (SelectedDownloader is not { CanRollback: true, Downloader: var downloader } row)
        {
            return;
        }

        await RunBusy($"Rolling back {downloader.Name}", async (_, _) =>
        {
            var from = await ToolManager.GetVersionAsync(ToolManager.ManagedPath(downloader));
            await ToolManager.RollbackAsync(downloader);
            if (from is not null)
            {
                Settings.SkippedVersions[downloader.Id] = from;
            }
            return $"{downloader.Name} rolled back {from} → {row.PreviousVersion}. The daily update check will skip {from}; " +
                   "press Roll back again to undo.";
        });
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ReinstallSelected()
    {
        if (SelectedDownloader is not { Downloader: { IsManaged: true } downloader })
        {
            return;
        }

        await RunBusy($"Re-downloading {downloader.Name}", async (progress, ct) =>
        {
            await ToolManager.InstallAsync(downloader, progress, ct);
            return $"{downloader.Name} re-downloaded.";
        });
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task SetDefault()
    {
        if (SelectedDownloader is null)
        {
            return;
        }

        Settings.DefaultDownloaderId = SelectedDownloader.Downloader.Id;
        _host.SetStatus($"Default downloader: {SelectedDownloader.Downloader.Name}");
        foreach (var row in Downloaders)
        {
            row.IsDefault = row.Downloader.Id == Settings.DefaultDownloaderId;
        }

        if (SelectedDownloader.Downloader.IsManaged && !ToolManager.IsInstalledByApp(SelectedDownloader.Downloader))
        {
            await InstallOrUpdate(SelectedDownloader.Downloader, auto: false);
        }

        await RefreshAsync(); // the setup checklist checks the default downloader
    }

    [RelayCommand]
    private async Task RemoveSelected()
    {
        if (SelectedDownloader is not { Downloader: { IsManaged: false } downloader })
        {
            return;
        }

        Settings.Downloaders.Remove(downloader);
        Settings.RemoveDanglingReferences();
        SelectedDownloader = null;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task BrowseNewDownloader()
    {
        if (await _host.Dialogs.PickFileAsync("Select downloader executable") is { } path)
        {
            NewDownloaderPath = path;
            if (string.IsNullOrWhiteSpace(NewDownloaderName))
            {
                NewDownloaderName = Path.GetFileNameWithoutExtension(path);
            }

            if (Path.GetFileName(path).Contains("youtube-dl", StringComparison.OrdinalIgnoreCase))
            {
                NewDownloaderFlavor = DownloaderFlavor.YoutubeDl;
            }
        }
    }

    [RelayCommand]
    private async Task AddCustomDownloader()
    {
        if (!File.Exists(NewDownloaderPath))
        {
            ToolOutput = "Choose an existing executable first.";
            return;
        }
        var downloader = new Downloader
        {
            Name = string.IsNullOrWhiteSpace(NewDownloaderName) ? Path.GetFileNameWithoutExtension(NewDownloaderPath) : NewDownloaderName.Trim(),
            ExecutablePath = NewDownloaderPath,
            Flavor = NewDownloaderFlavor,
        };
        Settings.Downloaders.Add(downloader);
        NewDownloaderName = NewDownloaderPath = "";
        await RefreshAsync();
        SelectedDownloader = Downloaders.FirstOrDefault(r => r.Downloader == downloader);
        ToolOutput = $"Added {downloader.Name}. Use \"Set as default\", or pick it for a preset on the Presets tab.";
    }

    // ---------- cookies ----------

    public string CookieValueHint => NewCookieKind == CookieSourceKind.File
        ? "Path to a cookies.txt file (Netscape format, e.g. exported with a \"Get cookies.txt\" browser extension)"
        : "yt-dlp browser spec, e.g. firefox:work-profile or chrome:Profile 2";

    partial void OnNewCookieKindChanged(CookieSourceKind value) => OnPropertyChanged(nameof(CookieValueHint));

    [RelayCommand]
    private async Task BrowseCookieFile()
    {
        if (await _host.Dialogs.PickFileAsync("Select cookies.txt") is { } path)
        {
            NewCookieKind = CookieSourceKind.File;
            NewCookieValue = path;
            if (string.IsNullOrWhiteSpace(NewCookieName))
            {
                NewCookieName = Path.GetFileNameWithoutExtension(path);
            }
        }
    }

    [RelayCommand]
    private void AddCookieSource()
    {
        var value = NewCookieValue.Trim().Trim('"');
        if (value.Length == 0)
        {
            CookieMessage = NewCookieKind == CookieSourceKind.File ? "Choose a cookies.txt file first." : "Enter a browser spec first.";
            return;
        }
        if (NewCookieKind == CookieSourceKind.File && !File.Exists(value))
        {
            CookieMessage = "That file doesn't exist.";
            return;
        }
        var source = new CookieSource
        {
            Name = !string.IsNullOrWhiteSpace(NewCookieName) ? NewCookieName.Trim()
                : NewCookieKind == CookieSourceKind.File ? Path.GetFileNameWithoutExtension(value) : value,
            Kind = NewCookieKind,
            Value = value,
        };
        Settings.CookieSources.Add(source);
        NewCookieName = NewCookieValue = "";
        SelectedCookieSource = source;
        CookieMessage = $"Added \"{source.Name}\". Pick it in the Cookies list on the Downloads tab.";
    }

    [RelayCommand]
    private void RemoveCookieSource()
    {
        if (SelectedCookieSource is not { } source)
        {
            return;
        }

        Settings.CookieSources.Remove(source);
        Settings.RemoveDanglingReferences();
        CookieMessage = $"Removed \"{source.Name}\".";
    }

    [RelayCommand]
    private async Task DownloadFfmpeg()
    {
        await RunBusy("Downloading ffmpeg (≈100 MB)", async (progress, ct) =>
        {
            await ToolManager.DownloadFfmpegAsync(progress, ct);
            return "ffmpeg installed to " + AppPaths.BinDir;
        });
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task BrowseFfmpeg()
    {
        if (await _host.Dialogs.PickFileAsync("Select ffmpeg executable") is { } path)
        {
            Settings.FfmpegPath = path;
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task ResetFfmpeg()
    {
        Settings.FfmpegPath = null;
        await RefreshAsync();
    }

    [RelayCommand]
    private Task OpenDataFolder() => _host.Dialogs.OpenFolderAsync(AppPaths.DataDir);

    private async Task RunBusy(string what, Func<IProgress<TransferProgress>, CancellationToken, Task<string>> action, bool quietOnError = false)
    {
        if (Busy.IsActive)
        {
            return;
        }

        Setup.ShowWaiting(what);
        // The live text goes to the status bar too, so it's visible from every tab.
        var result = await Busy.RunAsync(what, action, _host.SetStatus);
        switch (result.Outcome)
        {
            case JobOutcome.Succeeded:
                ToolOutput = result.Message;
                _host.SetStatus(result.Message.Split('\n').Last());
                break;
            case JobOutcome.Cancelled:
                ToolOutput = $"{what}: cancelled.";
                _host.SetStatus(ToolOutput);
                break;
            case JobOutcome.Failed:
                ToolOutput = $"{what} failed: {result.Message}";
                if (!quietOnError)
                {
                    _host.SetStatus(ToolOutput);
                }

                break;
        }
    }
}
