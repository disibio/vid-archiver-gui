using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.ViewModels;

public enum DownloadState
{
    Resolving,
    Ready,
    Queued,
    Downloading,
    Completed,
    Skipped,
    Failed,
    Cancelled,
}

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
    public Engine? EngineOverride
    {
        get => _engineOverride;
        set
        {
            if (SetProperty(ref _engineOverride, value))
            {
                UpdateDetails();
            }
        }
    }
    private Engine? _engineOverride;

    /// <summary>The downloader this item uses: <see cref="EngineOverride"/>, else the preset's.</summary>
    internal Engine Engine => EngineOverride ?? _owner.Settings.EngineFor(Preset);

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(CanStart), nameof(CanCancel), nameof(CanEdit), nameof(IsFinished), nameof(ShowProgress), nameof(CanRemember))]
    private DownloadState _state = DownloadState.Resolving;

    /// <summary>True once the user picks a folder by hand, so re-applying rules won't overwrite it.</summary>
    public bool DestinationEdited { get; set; }

    /// <summary>Put back from the last session: its preset and cookies were already chosen, so rules don't change them.</summary>
    internal bool Restored { get; set; }

    public bool CanStart => State is DownloadState.Ready or DownloadState.Failed or DownloadState.Cancelled;
    public bool CanCancel => State is DownloadState.Resolving or DownloadState.Queued or DownloadState.Downloading;
    public bool CanEdit => State is DownloadState.Ready or DownloadState.Queued or DownloadState.Failed or DownloadState.Cancelled;
    public bool IsFinished => State is DownloadState.Completed or DownloadState.Skipped;
    public bool ShowProgress => State is DownloadState.Downloading or DownloadState.Completed or DownloadState.Resolving;
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

        if (Engine != _owner.Settings.DefaultEngine)
        {
            parts.Add("via " + Engine.Name);
        }

        if (!Cookies.IsNone(CookieId))
        {
            parts.Add("cookies: " + Cookies.DisplayName(_owner.Settings, CookieId));
        }

        Details = string.Join("  ·  ", parts);
    }

    partial void OnPresetChanged(Preset value)
    {
        EngineOverride = null; // the new preset may use another downloader
        UpdateDetails();
    }

    // ---------- commands ----------

    [RelayCommand] private void Start() => _owner.Enqueue(this);

    [RelayCommand] private void Cancel() => CancelInternal();

    [RelayCommand] private void Remove() => _owner.Remove(this);

    [RelayCommand] private Task Browse() => _owner.BrowseDestinationAsync(this);

    [RelayCommand] private Task OpenFolder() => _owner.OpenFolderAsync(this);

    [RelayCommand] private void Remember() => _owner.CreateRuleFrom(this);

    [RelayCommand] private Task CopyUrl() => _owner.CopyAsync(Url, "URL");

    [RelayCommand] private Task CopyTitle() => _owner.CopyAsync(Title, "title");

    [RelayCommand] private Task CopyFolder() => _owner.CopyAsync(Destination, "folder");

    [RelayCommand] private void AddAgain() => _owner.AddAgain(this);

    internal void CancelInternal()
    {
        if (State == DownloadState.Queued)
        {
            State = DownloadState.Cancelled;
        }

        _cts?.Cancel();
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
    internal async Task<bool> ReadInfoAsync(AppSettings settings, CancellationToken ct)
    {
        var presetArgs = ArgumentParser.Split(Preset.Arguments);
        var result = await Resilience.RunAsync(settings, Engine,
            async (engine, attemptCt) =>
            {
                try
                {
                    var cookieArgs = Cookies.Args(settings, CookieId, engine.Flavor);
                    Info = await MetadataService.FetchAsync(Url, presetArgs, engine, cookieArgs, attemptCt);
                    return null;
                }
                catch (YtDlpException e)
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

        EngineOverride = result.Engine == settings.EngineFor(Preset) ? null : result.Engine;
        return true;
    }

    // ---------- download ----------

    internal async Task DownloadAsync(AppSettings settings)
    {
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

        try
        {
            var primary = Engine;
            var presetArgs = ArgumentParser.Split(Preset.Arguments);
            var ffmpeg = ToolManager.ResolveFfmpeg(settings.FfmpegPath);
            var result = await Resilience.RunAsync(settings, primary,
                async (engine, ct) =>
                {
                    var request = new DownloadRequest(Url, presetArgs, Destination)
                    {
                        CookieArgs = Cookies.Args(settings, CookieId, engine.Flavor),
                        Gentle = settings.GentleDownloads,
                    };
                    _lastError = null;
                    var exitCode = await DownloadRunner.RunAsync(request, engine, ffmpeg, OnOutput, ct);
                    // Let any output events still queued on the UI thread land first.
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    return exitCode == 0 ? null : _lastError ?? $"{engine.Engine.Name} exited with code {exitCode}";
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
                if (result.Engine != primary)
                {
                    EngineOverride = result.Engine;
                }

                var skipped = _alreadyDoneCount > 0 && _destinationCount == 0;
                State = skipped ? DownloadState.Skipped : DownloadState.Completed;
                IsIndeterminate = false;
                Progress = 100;
                ProgressText = skipped ? "Nothing new — already in archive" : "Finished";
            }
            else
            {
                State = DownloadState.Failed;
                Error = result.Error + AdviceFor(result.Kind);
            }
        }
        catch (OperationCanceledException)
        {
            State = DownloadState.Cancelled;
            ProgressText = "Cancelled";
        }
        catch (Exception e)
        {
            State = DownloadState.Failed;
            Error = e.Message;
        }
        finally
        {
            IsIndeterminate = false;
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
                IsIndeterminate = p.Fraction is null;
                Progress = (p.Fraction ?? 0) * 100;
                ProgressText = _itemPrefix + (p.Fraction is { } f
                    ? $"{f * 100:0.0}% of {DisplayFormat.Bytes(p.TotalBytes)}  ·  {DisplayFormat.Bytes(p.Speed)}/s  ·  ETA {DisplayFormat.Eta(p.Eta)}"
                    : $"{DisplayFormat.Bytes(p.DownloadedBytes)}  ·  {DisplayFormat.Bytes(p.Speed)}/s");
                return; // progress lines are too chatty for the log
            case OutputEvent.PlaylistItem pi:
                _itemPrefix = $"Item {pi.Index}/{pi.Count}  ·  ";
                ProgressText = _itemPrefix + "starting…";
                AppendLog($"[download] Downloading item {pi.Index} of {pi.Count}");
                return;
            case OutputEvent.PostProcessing pp:
                IsIndeterminate = true;
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
