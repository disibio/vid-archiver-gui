using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.ViewModels;

public partial class DownloadItemViewModel : ObservableObject
{
    private const int MaxLogLines = 500;

    private readonly DownloadsViewModel _owner;
    private readonly List<string> _log = [];
    private CancellationTokenSource? _cts;
    private bool _settingRoutedDestination;

    // Written from the process output thread, read after the process exits.
    private int _alreadyDoneCount;
    private int _destinationCount;
    private string? _lastError;
    private string _itemPrefix = "";

    // The playlist item being downloaded (1-based) and how many there are; 0 for a single video.
    private int _playlistIndex;
    private int _playlistCount;

    public DownloadItemViewModel(DownloadsViewModel owner, string url, Preset preset)
    {
        _owner = owner;
        Url = url;
        _title = url;
        _preset = preset;
    }

    public string Url { get; }

    public ObservableCollection<Preset> Presets => _owner.Presets;
    public ObservableCollection<Choice> CookieChoices => _owner.CookieChoices;

    /// <summary>Cookie source for this download (see <see cref="Cookies"/>); null = no cookies.</summary>
    public string? CookieId
    {
        get => _cookieId;
        set
        {
            if (!SetProperty(ref _cookieId, Cookies.IsNone(value) ? null : value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedCookie));
            UpdateDetails();
        }
    }
    private string? _cookieId;

    public Choice? SelectedCookie
    {
        get => _owner.FindCookieChoice(CookieId);
        set
        {
            if (value is not null)
            {
                CookieId = value.Id;
            }
        }
    }

    internal void OnCookieChoicesChanged() => OnPropertyChanged(nameof(SelectedCookie));

    /// <summary>A fallback downloader that worked for this URL when the preset's own one didn't; used for the download too.</summary>
    public Downloader? DownloaderOverride
    {
        get => _downloaderOverride;
        set
        {
            if (SetProperty(ref _downloaderOverride, value))
            {
                UpdateDetails();
            }
        }
    }
    private Downloader? _downloaderOverride;

    /// <summary>The downloader this item uses: <see cref="DownloaderOverride"/>, else the preset's.</summary>
    internal Downloader Downloader => DownloaderOverride ?? _owner.Settings.DownloaderFor(Preset);

    private string AdviceFor(FailureKind kind) =>
        Resilience.Advice(kind, !Cookies.IsNone(CookieId)) is { } advice ? Environment.NewLine + advice : "";

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private MediaInfo? _info;
    [ObservableProperty] private string _destination = "";
    [ObservableProperty] private Preset _preset;
    [ObservableProperty] private string _routeDescription = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _logText = "";

    /// <summary>The finished file's thumbnail, from the file itself or the one saved beside it; null if it has none.</summary>
    [ObservableProperty] private Bitmap? _thumbnail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(CanStart), nameof(StartText), nameof(CanPause), nameof(CanCancel), nameof(CanEdit),
        nameof(IsFinished), nameof(ShowProgress), nameof(CanRemember))]
    private DownloadState _state = DownloadState.Resolving;

    /// <summary>True once the user picks a folder by hand, so re-applying rules won't overwrite it.</summary>
    public bool DestinationEdited { get; set; }

    /// <summary>Put back from last session and its info not read yet: its preset and cookies were already chosen, so rules don't change them.</summary>
    internal bool Restored { get; set; }

    /// <summary>
    /// Pause was pressed while it was downloading or reading its info (or it was paused last session), so it becomes
    /// paused once that stops. Starting it clears this.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    private bool _pauseRequested;

    /// <summary>The file the last download produced (for a playlist, its last one), if known.</summary>
    internal string? LastFile { get; private set; }

    public bool CanStart => State is DownloadState.Ready or DownloadState.Failed or DownloadState.Cancelled or DownloadState.Paused;
    public string StartText => State == DownloadState.Paused ? "Resume" : "Start";
    public bool CanPause => State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Resolving && !PauseRequested;
    public bool CanCancel => State is DownloadState.Resolving or DownloadState.Queued or DownloadState.Downloading or DownloadState.Paused;
    public bool CanEdit => State is DownloadState.Ready or DownloadState.Queued or DownloadState.Failed or DownloadState.Cancelled or DownloadState.Paused;
    public bool IsFinished => State is DownloadState.Completed or DownloadState.Skipped;
    public bool ShowProgress => State is DownloadState.Downloading or DownloadState.Completed or DownloadState.Resolving or DownloadState.Paused;
    public bool CanRemember => Info is not null && CanEdit;

    public string StateText => State switch
    {
        DownloadState.Resolving => "Reading info…",
        DownloadState.Ready => "Ready",
        DownloadState.Queued => "Queued",
        DownloadState.Downloading => "Downloading",
        DownloadState.Completed => "Done",
        DownloadState.Skipped => "Already archived",
        DownloadState.Failed => "Failed",
        DownloadState.Cancelled => "Cancelled",
        DownloadState.Paused => "Paused",
        _ => State.ToString(),
    };

    partial void OnStateChanged(DownloadState value) => _owner.OnItemStateChanged();

    partial void OnDestinationChanged(string value)
    {
        if (!_settingRoutedDestination)
        {
            DestinationEdited = true;
        }
    }

    partial void OnInfoChanged(MediaInfo? value)
    {
        OnPropertyChanged(nameof(CanRemember));
        UpdateDetails();
    }

    partial void OnRouteDescriptionChanged(string value) => UpdateDetails();

    public void SetRoutedDestination(string destination)
    {
        _settingRoutedDestination = true;
        Destination = destination;
        _settingRoutedDestination = false;
        DestinationEdited = false;
    }

    private void UpdateDetails()
    {
        if (Info is not { } i)
        {
            return;
        }

        Title = i.Title ?? Url;
        var parts = new List<string>();
        if (i.Site is { } site)
        {
            parts.Add(site);
        }

        if (i.Channel is { } ch)
        {
            parts.Add(ch);
        }

        if (i.IsPlaylist)
        {
            parts.Add(i.EntryCount is { } n ? $"Playlist · {n} items" : "Playlist");
        }
        else if (i.Playlist is { } pl)
        {
            parts.Add("Playlist: " + pl);
        }

        if (RouteDescription.Length > 0)
        {
            parts.Add(RouteDescription);
        }

        if (Downloader != _owner.Settings.DefaultDownloader)
        {
            parts.Add("via " + Downloader.Name);
        }

        if (!Cookies.IsNone(CookieId))
        {
            parts.Add("cookies: " + Cookies.DisplayName(_owner.Settings, CookieId));
        }

        Details = string.Join("  ·  ", parts);
    }

    partial void OnPresetChanged(Preset value)
    {
        DownloaderOverride = null; // the new preset may use another downloader
        UpdateDetails();
    }

    // ---------- commands ----------

    [RelayCommand] private void Start() => _owner.Enqueue(this);

    [RelayCommand] private void Pause() => PauseInternal();

    [RelayCommand] private void Cancel() => CancelInternal();

    [RelayCommand] private Task Remove() => RemoveAsync();

    /// <summary>See <see cref="DownloadsViewModel.RemoveAsync"/>.</summary>
    internal Task<bool> RemoveAsync() => _owner.RemoveAsync(this);

    [RelayCommand] private Task Browse() => _owner.BrowseDestinationAsync(this);

    [RelayCommand] private Task OpenFolder() => _owner.OpenFolderAsync(this);

    [RelayCommand] private void Remember() => _owner.CreateRuleFrom(this);

    [RelayCommand] private Task CopyUrl() => _owner.CopyAsync(Url, "URL");

    [RelayCommand] private Task CopyTitle() => _owner.CopyAsync(Title, "title");

    [RelayCommand] private Task CopyFolder() => _owner.CopyAsync(Destination, "folder");

    [RelayCommand] private void AddAgain() => _owner.AddAgain(this);

    internal void CancelInternal()
    {
        if (State is DownloadState.Queued or DownloadState.Paused)
        {
            State = DownloadState.Cancelled;
        }

        PauseRequested = false;
        _cts?.Cancel();
    }

    /// <summary>
    /// Stops a running download (yt-dlp leaves its partly downloaded files and carries on from them next time), or
    /// holds a queued one, or one whose info is still being read.
    /// </summary>
    internal void PauseInternal()
    {
        if (State == DownloadState.Queued)
        {
            State = DownloadState.Paused;
            ProgressText = "Paused";
        }
        else if (State is DownloadState.Downloading or DownloadState.Resolving)
        {
            PauseRequested = true;
            if (State == DownloadState.Downloading)
            {
                _cts?.Cancel();
            }
        }
    }

    internal CancellationToken BeginOperation()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    // ---------- reading info ----------

    /// <summary>
    /// Reads the video's info with this item's downloader, falling back to others if extraction is broken. Returns
    /// false, with <see cref="Error"/> set, if no downloader could read it.
    /// </summary>
    internal async Task<bool> ReadInfoAsync(CancellationToken ct)
    {
        var settings = _owner.Settings;
        var presetArgs = ArgumentParser.Split(Preset.Arguments);
        var result = await Resilience.RunAsync(settings, Downloader,
            async (downloader, attemptCt) =>
            {
                try
                {
                    var cookieArgs = Cookies.Args(settings, CookieId, downloader.Flavor);
                    Info = await MetadataService.FetchAsync(Url, presetArgs, downloader, cookieArgs, attemptCt);
                    return null;
                }
                catch (DownloaderException e)
                {
                    AppendLog("ERROR: " + e.Message);
                    return e.Message;
                }
            },
            message => Dispatcher.UIThread.Post(() => { ProgressText = message; AppendLog(message); }),
            ct);

        if (!result.Succeeded)
        {
            State = DownloadState.Failed;
            Error = "Could not read info: " + result.Error + AdviceFor(result.Kind);
            return false;
        }

        DownloaderOverride = result.Downloader == settings.DownloaderFor(Preset) ? null : result.Downloader;
        return true;
    }

    // ---------- download ----------

    internal async Task DownloadAsync()
    {
        var settings = _owner.Settings;
        var token = BeginOperation();
        State = DownloadState.Downloading;
        Error = null;
        Progress = 0;
        IsIndeterminate = true;
        ProgressText = "Starting…";
        _alreadyDoneCount = 0;
        _destinationCount = 0;
        _lastError = null;
        _itemPrefix = "";
        _playlistIndex = 0;
        _playlistCount = 0;
        LastFile = null;
        Thumbnail = null;

        try
        {
            var primary = Downloader;
            var presetArgs = ArgumentParser.Split(Preset.Arguments);
            var ffmpeg = ToolManager.ResolveFfmpeg(settings.FfmpegPath);
            var result = await Resilience.RunAsync(settings, primary,
                async (downloader, ct) =>
                {
                    var request = new DownloadRequest(Url, presetArgs, Destination)
                    {
                        CookieArgs = Cookies.Args(settings, CookieId, downloader.Flavor),
                        Gentle = settings.GentleDownloads,
                        IgnoreErrors = settings.IgnoreExtraErrors,
                    };
                    _lastError = null;
                    var run = await DownloadRunner.RunAsync(request, downloader, ffmpeg, OnOutput, ct);
                    LastFile = run.Files.LastOrDefault() ?? LastFile;
                    // Let any output events still queued on the UI thread land first.
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    return run.ExitCode == 0 ? null : _lastError ?? $"{downloader.Downloader.Name} exited with code {run.ExitCode}";
                },
                message => Dispatcher.UIThread.Post(() =>
                {
                    Error = null;
                    IsIndeterminate = true;
                    ProgressText = message;
                    AppendLog(message);
                }),
                token);

            if (result.Succeeded)
            {
                if (result.Downloader != primary)
                {
                    DownloaderOverride = result.Downloader;
                }

                var skipped = _alreadyDoneCount > 0 && _destinationCount == 0;
                State = skipped ? DownloadState.Skipped : DownloadState.Completed;
                IsIndeterminate = false;
                Progress = 100;
                ProgressText = skipped ? "Nothing new — already in archive" : "Finished";
                if (LastFile is { } file)
                {
                    _ = LoadThumbnailAsync(file);
                }
            }
            else
            {
                State = DownloadState.Failed;
                Error = result.Error + AdviceFor(result.Kind);
            }
        }
        catch (OperationCanceledException)
        {
            State = PauseRequested ? DownloadState.Paused : DownloadState.Cancelled;
            ProgressText = PauseRequested ? "Paused" : "Cancelled";
        }
        catch (Exception e)
        {
            State = DownloadState.Failed;
            Error = e.Message;
        }
        finally
        {
            PauseRequested = false;
            IsIndeterminate = false;
        }
    }

    /// <summary>
    /// Shows the thumbnail saved beside <paramref name="file"/> (--write-thumbnail), else the one embedded in it
    /// (--embed-thumbnail), read with ffmpeg. Only local files are read; a missing or unreadable one shows nothing.
    /// </summary>
    private async Task LoadThumbnailAsync(string file)
    {
        string? extracted = null;
        try
        {
            var image = Thumbnails.FindSidecar(file);
            if (image is null && ToolManager.ResolveFfmpeg(_owner.Settings.FfmpegPath) is { } ffmpeg)
            {
                extracted = Path.Combine(Path.GetTempPath(), $"vidarchivergui-thumbnail-{Guid.NewGuid():N}.png");
                image = await Thumbnails.ExtractEmbeddedAsync(file, ffmpeg, extracted) ? extracted : null;
            }

            if (image is not null)
            {
                Thumbnail = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(image);
                    return Bitmap.DecodeToWidth(stream, 192);
                });
            }
        }
        catch (Exception e)
        {
            AppendLog("Couldn't show the thumbnail: " + e.Message); // it's only a picture; the download is fine
        }
        finally
        {
            if (extracted is not null)
            {
                File.Delete(extracted);
            }
        }
    }

    private void OnOutput(OutputEvent e)
    {
        switch (e)
        {
            case OutputEvent.AlreadyDone:
                Interlocked.Increment(ref _alreadyDoneCount);
                break;
            case OutputEvent.Destination:
                Interlocked.Increment(ref _destinationCount);
                break;
            case OutputEvent.Error err:
                _lastError = err.Message;
                break;
        }
        Dispatcher.UIThread.Post(() => Apply(e));
    }

    private void Apply(OutputEvent e)
    {
        switch (e)
        {
            case OutputEvent.Progress { Value: var p }:
                if (_playlistCount > 0)
                {
                    // The bar shows the whole playlist; the text still shows the current file.
                    IsIndeterminate = false;
                    Progress = DownloadQueue.PlaylistProgress(_playlistIndex, _playlistCount, p.Fraction ?? 0, Progress / 100) * 100;
                }
                else
                {
                    IsIndeterminate = p.Fraction is null;
                    Progress = (p.Fraction ?? 0) * 100;
                }

                ProgressText = _itemPrefix + (p.Fraction is { } f
                    ? $"{f * 100:0.0}% of {DisplayFormat.Bytes(p.TotalBytes)}  ·  {DisplayFormat.Bytes(p.Speed)}/s  ·  ETA {DisplayFormat.Eta(p.Eta)}"
                    : $"{DisplayFormat.Bytes(p.DownloadedBytes)}  ·  {DisplayFormat.Bytes(p.Speed)}/s");
                return; // progress lines are too chatty for the log
            case OutputEvent.PlaylistItem pi:
                _playlistIndex = pi.Index;
                _playlistCount = pi.Count;
                IsIndeterminate = false;
                Progress = DownloadQueue.PlaylistProgress(pi.Index, pi.Count, 0, Progress / 100) * 100;
                _itemPrefix = $"Item {pi.Index}/{pi.Count}  ·  ";
                ProgressText = _itemPrefix + "starting…";
                AppendLog($"[download] Downloading item {pi.Index} of {pi.Count}");
                return;
            case OutputEvent.PostProcessing pp:
                IsIndeterminate = _playlistCount == 0;
                ProgressText = _itemPrefix + $"Post-processing ({pp.Step})…";
                break;
            case OutputEvent.Error err:
                Error = err.Message;
                AppendLog("ERROR: " + err.Message);
                return;
        }

        AppendLog(e switch
        {
            OutputEvent.Text t => t.Line,
            OutputEvent.Destination d => "→ " + d.Path,
            OutputEvent.AlreadyDone a => a.Message,
            OutputEvent.PostProcessing pp => $"[{pp.Step}]",
            _ => e.ToString(),
        });
    }

    internal void AppendLog(string line)
    {
        _log.Add(line);
        if (_log.Count > MaxLogLines)
        {
            _log.RemoveRange(0, _log.Count - MaxLogLines);
        }

        LogText = string.Join(Environment.NewLine, _log);
    }
}
