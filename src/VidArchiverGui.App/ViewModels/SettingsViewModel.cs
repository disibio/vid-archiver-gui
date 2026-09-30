using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VidArchiverGui.App.Services;
using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.ViewModels;

public partial class EngineRowViewModel(Engine engine) : ObservableObject
{
    public Engine Engine => engine;
    public string Source => engine.IsManaged ? "Installed from github.com/" + engine.GitHubRepo : "Custom executable";
    public string FlavorText => engine.Flavor == EngineFlavor.YtDlp ? "yt-dlp compatible" : "youtube-dl compatible";

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
    private readonly AppHost host;

    public SettingsViewModel(AppHost host, SetupViewModel setup)
    {
        this.host = host;
        Setup = setup;
        setup.ToolsChanged += async () =>
        {
            try { await RefreshToolsAsync(); }
            catch (Exception e) { ToolOutput = "Refresh failed: " + e.Message; }
        };
    }

    // Refreshes can be triggered back to back (e.g. several installs from "Fix now"); run them one at a time.
    private readonly SemaphoreSlim _refreshGate = new(1);

    public SetupViewModel Setup { get; }
    public AppSettings Settings => host.Settings;
    public string DataFolder => AppPaths.DataDir;
    public string DataFolderHint => AppPaths.IsPackaged
        ? "Settings, rules and presets live in settings.json here. Windows removes this folder when the app is uninstalled."
        : "Settings, rules and presets live in settings.json here. Put a file named portable.txt next to the app to keep data beside it instead.";
    public bool CanDownloadFfmpeg => ToolManager.CanDownloadFfmpeg;
    public string FfmpegHint => ToolManager.FfmpegInstallHint;
    public static EngineFlavor[] Flavors { get; } = Enum.GetValues<EngineFlavor>();
    public static AppTheme[] Themes { get; } = Enum.GetValues<AppTheme>();
    public static CookieSourceKind[] CookieKinds { get; } = Enum.GetValues<CookieSourceKind>();

    /// <summary>Theme changes apply immediately; save right away so they stick even without pressing Save.</summary>
    public AppTheme Theme
    {
        get => Settings.Theme;
        set
        {
            if (Settings.Theme == value)
            {
                return;
            }

            Settings.Theme = value;
            host.Save(quiet: true);
            OnPropertyChanged();
        }
    }

    public ObservableCollection<EngineRowViewModel> Engines { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanRemoveSelected), nameof(InstallButtonText))]
    private EngineRowViewModel? _selectedEngine;

    [ObservableProperty] private string _newEngineName = "";
    [ObservableProperty] private string _newEnginePath = "";
    [ObservableProperty] private EngineFlavor _newEngineFlavor = EngineFlavor.YtDlp;

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

    public bool HasSelection => SelectedEngine is not null;
    public bool CanRemoveSelected => SelectedEngine is { Engine.IsManaged: false };
    public string InstallButtonText => SelectedEngine?.Engine switch
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

        // The installs below skip their usual refresh; one runs at the end instead.
        _initializing = true;
        try
        {
            // Prefer our own copy over whatever is on PATH (e.g. an outdated pip install) so it can be kept current.
            var engine = Settings.DefaultEngine;
            if (engine.IsManaged && !ToolManager.IsInstalledByApp(engine))
            {
                await InstallOrUpdate(engine, auto: false);
            }
            else if (Settings.AutoUpdateYtDlp
                     && (Settings.LastYtDlpUpdateCheck is null || DateTimeOffset.Now - Settings.LastYtDlpUpdateCheck > TimeSpan.FromHours(24)))
            {
                foreach (var e in Settings.Engines.Where(ToolManager.IsInstalledByApp).ToList())
                {
                    await InstallOrUpdate(e, auto: true);
                }

                if (ToolManager.IsDenoInstalledByApp)
                {
                    await UpdateDenoIfNewer();
                }

                Settings.LastYtDlpUpdateCheck = DateTimeOffset.Now;
                host.Save(quiet: true);
            }
        }
        finally
        {
            _initializing = false;
        }

        await RefreshAsync();
    }

    private bool _initializing;

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
        var installed = Settings.Engines.Where(e => ToolManager.LocatePath(e) is not null).Select(e => e.Name).ToList();
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

        var selectedId = SelectedEngine?.Engine.Id;
        var rows = Settings.Engines.Select(e => new EngineRowViewModel(e) { IsDefault = e.Id == Settings.DefaultEngineId }).ToList();
        Engines.Clear();
        foreach (var row in rows)
        {
            Engines.Add(row);
        }

        SelectedEngine = Engines.FirstOrDefault(r => r.Engine.Id == selectedId) ?? Engines.FirstOrDefault(r => r.IsDefault);

        // All at once: each "--version" can take several seconds (see ToolManager.GetVersionAsync).
        await Task.WhenAll(rows.Select(async row =>
        {
            var path = ToolManager.LocatePath(row.Engine);
            row.IsAvailable = path is not null;
            if (path is null)
            {
                row.Status = row.Engine.IsManaged ? "Not installed" : "Executable not found";
                return;
            }

            row.Status = $"Running \"{Path.GetFileName(path)} --version\"…  —  {path}";
            row.Status = $"{await ToolManager.GetVersionAsync(path) ?? "version unknown (--version failed or didn't answer)"}  —  {path}";
            row.PreviousVersion = ToolManager.CanRollback(row.Engine)
                ? await ToolManager.GetVersionAsync(ToolManager.PreviousPath(row.Engine)) ?? "previous version"
                : null;
        }));

        var ff = host.Tools.ResolveFfmpeg();
        FfmpegStatus = ff is null
            ? "Not found — merging video+audio and embedding thumbnails/subtitles need ffmpeg."
            : $"{await host.Tools.GetFfmpegVersionAsync() ?? "version unknown"}  —  {ff}";
    }

    [RelayCommand]
    private Task InstallSelected() => SelectedEngine is { } row ? InstallOrUpdate(row.Engine, auto: false) : Task.CompletedTask;

    private async Task InstallOrUpdate(Engine engine, bool auto)
    {
        if (!engine.IsManaged)
        {
            if (ToolManager.LocatePath(engine) is { } exe)
            {
                await RunBusy($"Running {engine.Name} -U", async (_, ct) => await ToolManager.SelfUpdateAsync(exe, ct));
            }

            return;
        }

        if (!ToolManager.IsInstalledByApp(engine))
        {
            await RunBusy($"Installing {engine.Name}", async (progress, ct) =>
            {
                await ToolManager.InstallAsync(engine, progress, ct);
                return $"{engine.Name} installed to {ToolManager.ManagedPath(engine)}";
            });
            return;
        }

        await RunBusy($"Checking for {engine.Name} updates", async (progress, ct) =>
        {
            var current = await ToolManager.GetVersionAsync(ToolManager.ManagedPath(engine), ct);
            var latest = await ToolManager.GetLatestVersionAsync(engine, ct);
            if (!ToolManager.IsNewer(latest, current))
            {
                return $"{engine.Name} is up to date ({current}).";
            }

            if (auto && latest is not null && Settings.SkippedVersions.GetValueOrDefault(engine.Id) == latest)
            {
                return $"{engine.Name} {latest} was rolled back, so it isn't re-installed automatically. Use \"Check for update\" to install it anyway.";
            }

            await ToolManager.InstallAsync(engine, progress, ct);
            if (Settings.SkippedVersions.Remove(engine.Id))
            {
                host.Save(quiet: true);
            }

            return $"{engine.Name} updated {current} → {latest}.";
        }, quietOnError: auto);
    }

    [RelayCommand]
    private async Task RollbackSelected()
    {
        if (SelectedEngine is not { CanRollback: true, Engine: var engine } row)
        {
            return;
        }

        await RunBusy($"Rolling back {engine.Name}", async (_, _) =>
        {
            var from = await ToolManager.GetVersionAsync(ToolManager.ManagedPath(engine));
            await ToolManager.RollbackAsync(engine);
            if (from is not null)
            {
                Settings.SkippedVersions[engine.Id] = from;
                host.Save(quiet: true);
            }
            return $"{engine.Name} rolled back {from} → {row.PreviousVersion}. The daily update check will skip {from}; " +
                   "press Roll back again to undo.";
        });
    }

    [RelayCommand]
    private async Task ReinstallSelected()
    {
        if (SelectedEngine is not { Engine: { IsManaged: true } engine })
        {
            return;
        }

        await RunBusy($"Re-downloading {engine.Name}", async (progress, ct) =>
        {
            await ToolManager.InstallAsync(engine, progress, ct);
            return $"{engine.Name} re-downloaded.";
        });
    }

    [RelayCommand]
    private async Task SetDefault()
    {
        if (SelectedEngine is null)
        {
            return;
        }

        Settings.DefaultEngineId = SelectedEngine.Engine.Id;
        host.Save(quiet: true);
        host.SetStatus($"Default downloader: {SelectedEngine.Engine.Name}");
        foreach (var row in Engines)
        {
            row.IsDefault = row.Engine.Id == Settings.DefaultEngineId;
        }

        if (SelectedEngine.Engine.IsManaged && !ToolManager.IsInstalledByApp(SelectedEngine.Engine))
        {
            await InstallOrUpdate(SelectedEngine.Engine, auto: false);
        }
    }

    [RelayCommand]
    private async Task RemoveSelected()
    {
        if (SelectedEngine is not { Engine: { IsManaged: false } engine })
        {
            return;
        }

        Settings.Engines.Remove(engine);
        Settings.RemoveDanglingReferences();
        host.Save(quiet: true);
        SelectedEngine = null;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task BrowseNewEngine()
    {
        if (await host.Dialogs.PickFileAsync("Select downloader executable") is { } path)
        {
            NewEnginePath = path;
            if (string.IsNullOrWhiteSpace(NewEngineName))
            {
                NewEngineName = Path.GetFileNameWithoutExtension(path);
            }

            if (Path.GetFileName(path).Contains("youtube-dl", StringComparison.OrdinalIgnoreCase))
            {
                NewEngineFlavor = EngineFlavor.YoutubeDl;
            }
        }
    }

    [RelayCommand]
    private async Task AddCustomEngine()
    {
        if (!File.Exists(NewEnginePath))
        {
            ToolOutput = "Choose an existing executable first.";
            return;
        }
        var engine = new Engine
        {
            Name = string.IsNullOrWhiteSpace(NewEngineName) ? Path.GetFileNameWithoutExtension(NewEnginePath) : NewEngineName.Trim(),
            ExecutablePath = NewEnginePath,
            Flavor = NewEngineFlavor,
        };
        Settings.Engines.Add(engine);
        host.Save(quiet: true);
        NewEngineName = NewEnginePath = "";
        await RefreshAsync();
        SelectedEngine = Engines.FirstOrDefault(r => r.Engine == engine);
        ToolOutput = $"Added {engine.Name}. Use \"Set as default\", or pick it for a preset on the Presets tab.";
    }

    // ---------- cookies ----------

    public string CookieValueHint => NewCookieKind == CookieSourceKind.File
        ? "Path to a cookies.txt file (Netscape format, e.g. exported with a \"Get cookies.txt\" browser extension)"
        : "yt-dlp browser spec, e.g. firefox:work-profile or chrome:Profile 2";

    partial void OnNewCookieKindChanged(CookieSourceKind value) => OnPropertyChanged(nameof(CookieValueHint));

    [RelayCommand]
    private async Task BrowseCookieFile()
    {
        if (await host.Dialogs.PickFileAsync("Select cookies.txt") is { } path)
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
        host.Save(quiet: true);
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
        host.Save(quiet: true);
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
    }

    [RelayCommand]
    private async Task BrowseFfmpeg()
    {
        if (await host.Dialogs.PickFileAsync("Select ffmpeg executable") is { } path)
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
    private Task OpenDataFolder() => host.Dialogs.OpenFolderAsync(AppPaths.DataDir);

    [RelayCommand]
    private void Save() => host.Save();

    private async Task RunBusy(string what, Func<IProgress<TransferProgress>, CancellationToken, Task<string>> action, bool quietOnError = false)
    {
        if (Busy.IsActive)
        {
            return;
        }

        Setup.ShowWaiting(what);
        // The live text goes to the status bar too, so it's visible from every tab.
        var result = await Busy.RunAsync(what, action, host.SetStatus);
        switch (result.Outcome)
        {
            case JobOutcome.Succeeded:
                ToolOutput = result.Message;
                host.SetStatus(result.Message.Split('\n').Last());
                break;
            case JobOutcome.Cancelled:
                ToolOutput = $"{what}: cancelled.";
                host.SetStatus(ToolOutput);
                break;
            case JobOutcome.Failed:
                ToolOutput = $"{what} failed: {result.Message}";
                if (!quietOnError)
                {
                    host.SetStatus(ToolOutput);
                }

                break;
        }

        if (!_initializing)
        {
            await RefreshAsync();
        }
    }
}
