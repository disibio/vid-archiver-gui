using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VidArchiverGui.App.Services;
using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.ViewModels;

public partial class DownloadsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly SemaphoreSlim _resolveGate = new(3);

    // Downloads that ended since the queue was last empty, for the "all done" notification.
    private readonly List<DownloadItemViewModel> _batch = [];

    // Put back from last session, waiting for ResumeRestored to read their info.
    private readonly List<DownloadItemViewModel> _restored = [];

    public DownloadsViewModel(AppHost host)
    {
        _host = host;
        _selectedPreset = host.Settings.DefaultPreset;
        RefreshCookieChoices();
        Items.CollectionChanged += (_, _) => UpdateSummary();
        host.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.MaxConcurrentDownloads))
            {
                Pump(); // start queued items right away if the limit was raised
            }
        };
    }

    internal void OnItemStateChanged() => UpdateSummary();

    public ObservableCollection<Preset> Presets => _host.Settings.Presets;
    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];
    public AppSettings Settings => _host.Settings;

    /// <summary>
    /// Shared by the Add bar and every item. Updated in place (so the drop-downs keep their items); selections are
    /// stored as ids, which is why the selected-item properties ignore the null a drop-down reports while it's cleared.
    /// </summary>
    public ObservableCollection<Choice> CookieChoices { get; } = [];

    public Choice? SelectedCookie
    {
        get => FindCookieChoice(_host.Settings.LastCookieId);
        set
        {
            if (value is null || value.Id == (_host.Settings.LastCookieId ?? Cookies.NoneId))
            {
                return;
            }

            _host.Settings.LastCookieId = Cookies.IsNone(value.Id) ? null : value.Id;
            OnPropertyChanged();
        }
    }

    internal Choice? FindCookieChoice(string? id) =>
        CookieChoices.FirstOrDefault(c => c.Id == (id ?? Cookies.NoneId)) ?? CookieChoices.FirstOrDefault();

    /// <summary>Called when the tab is shown, since browsers may have been installed or cookie sources edited.</summary>
    public void RefreshCookieChoices() => UpdateCookieChoices(null);

    /// <summary>
    /// Adds <paramref name="id"/> to <see cref="CookieChoices"/> if it's missing: a browser that isn't installed here,
    /// picked by a rule, is shown as such rather than as "No cookies".
    /// </summary>
    private void KeepCookieChoice(string? id)
    {
        if (id is not null && CookieChoices.All(c => c.Id != id))
        {
            UpdateCookieChoices(id);
        }
    }

    private void UpdateCookieChoices(string? alsoKeep)
    {
        var inUse = Items.Select(i => i.CookieId).Append(_host.Settings.LastCookieId).Append(alsoKeep);
        var choices = Cookies.Choices(_host.Settings, Cookies.DetectBrowsers(), inUse);
        if (choices.SequenceEqual(CookieChoices))
        {
            return;
        }

        CookieChoices.Clear();
        foreach (var c in choices)
        {
            CookieChoices.Add(c);
        }

        OnPropertyChanged(nameof(SelectedCookie));
        foreach (var item in Items)
        {
            item.OnCookieChoicesChanged();
        }
    }

    [ObservableProperty] private string _urlInput = "";
    [ObservableProperty] private Preset? _selectedPreset;
    [ObservableProperty] private DownloadItemViewModel? _selectedItem;
    [ObservableProperty] private string _summary = "";

    // ---------- adding ----------

    [RelayCommand]
    private void Add()
    {
        AddUrls(UrlInput);
        UrlInput = "";
    }

    /// <summary>Adds every link in <paramref name="text"/> (typed, pasted or dropped). Returns how many were new.</summary>
    public int AddUrls(string text)
    {
        var preset = SelectedPreset ?? _host.Settings.DefaultPreset;
        var added = 0;
        foreach (var url in DownloadQueue.ExtractUrls(text))
        {
            if (Items.Any(i => i.Url == url && !i.IsFinished && i.State != DownloadState.Cancelled))
            {
                continue;
            }

            var item = new DownloadItemViewModel(this, url, preset) { CookieId = _host.Settings.LastCookieId };
            Items.Add(item);
            _ = ResolveAsync(item);
            added++;
        }
        if (added == 0)
        {
            _host.SetStatus("No new URLs to add.");
        }

        return added;
    }

    /// <param name="startAfter">Queue it once its info is read, even if downloads don't start on their own.</param>
    private async Task ResolveAsync(DownloadItemViewModel item, bool startAfter = false)
    {
        var token = item.BeginOperation();
        item.State = DownloadState.Resolving;
        item.Error = null;
        item.IsIndeterminate = true;
        item.ProgressText = "Reading info…";
        try
        {
            await _resolveGate.WaitAsync(token);
            bool gotInfo;
            try
            {
                gotInfo = await item.ReadInfoAsync(token);
            }
            finally
            {
                _resolveGate.Release();
            }

            if (!gotInfo)
            {
                return;
            }

            ApplyRoute(item);
            if (item.RestoredPaused)
            {
                item.RestoredPaused = false;
                item.State = DownloadState.Paused;
                item.ProgressText = "Paused";
                return;
            }

            item.State = DownloadState.Ready;
            item.ProgressText = "";
            if (startAfter || _host.Settings.AutoStartDownloads)
            {
                Enqueue(item);
            }
        }
        catch (OperationCanceledException)
        {
            item.State = DownloadState.Cancelled;
        }
        catch (Exception e)
        {
            item.State = DownloadState.Failed;
            item.Error = "Could not read info: " + e.Message;
            item.AppendLog("ERROR: " + e.Message);
        }
        finally
        {
            item.IsIndeterminate = false;
            UpdateSummary();
        }
    }

    private void ApplyRoute(DownloadItemViewModel item)
    {
        if (item.Info is null)
        {
            return;
        }

        var route = Router.Resolve(item.Info, _host.Settings.Rules, _host.Settings.FallbackDestination);
        if (!item.DestinationEdited)
        {
            item.SetRoutedDestination(route.Destination);
            item.RouteDescription = route.Describe();
        }

        if (item.Restored)
        {
            item.Restored = false; // keep last session's preset and cookies; "Re-apply rules" still applies them in full
            return;
        }

        if (_host.Settings.FindPreset(route.PresetId) is { } preset)
        {
            item.Preset = preset;
        }

        if (route.CookieId is not null)
        {
            KeepCookieChoice(route.CookieId);
            item.CookieId = route.CookieId;
        }
    }

    [RelayCommand]
    private void ReapplyRules()
    {
        var count = 0;
        foreach (var item in Items.Where(i => i.CanEdit && !i.DestinationEdited && i.Info is not null))
        {
            ApplyRoute(item);
            count++;
        }
        _host.SetStatus($"Re-applied rules to {count} item(s).");
    }

    // ---------- queue ----------

    internal void Enqueue(DownloadItemViewModel item)
    {
        if (item.Info is null)
        {
            _ = ResolveAsync(item, startAfter: true); // metadata failed earlier: retry that first
            return;
        }
        if (string.IsNullOrWhiteSpace(item.Destination))
        {
            item.Error = "Choose a destination folder first.";
            return;
        }
        if (!Path.IsPathFullyQualified(item.Destination))
        {
            item.Error = $"\"{item.Destination}\" is not a full path. Use a complete folder such as E:\\Videos (check your folder rule).";
            return;
        }
        item.State = DownloadState.Queued;
        Pump();
    }

    private void Pump()
    {
        foreach (var next in DownloadQueue.ToStart(Items, i => i.State, _host.Settings.MaxConcurrentDownloads))
        {
            _ = DownloadAsync(next);
        }
        UpdateSummary();
    }

    private async Task DownloadAsync(DownloadItemViewModel item)
    {
        await item.DownloadAsync();
        if (item.State == DownloadState.Completed)
        {
            _host.SetStatus($"Finished: {item.Title}");
        }

        if (item.State is DownloadState.Completed or DownloadState.Skipped or DownloadState.Failed)
        {
            _batch.Add(item);
        }

        Pump();
        NotifyIfQueueDone();
    }

    private void NotifyIfQueueDone()
    {
        if (Items.Any(i => i.State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Resolving))
        {
            return;
        }

        var batch = _batch.ToList();
        _batch.Clear();
        if (batch.Count == 0 || !_host.Settings.NotifyWhenDone || _host.Dialogs.IsWindowActive)
        {
            return;
        }

        var done = batch.Count(i => i.State == DownloadState.Completed);
        var skipped = batch.Count(i => i.State == DownloadState.Skipped);
        var failed = batch.Count(i => i.State == DownloadState.Failed);
        _host.Dialogs.Notify(failed > 0 ? "Downloads finished, with errors" : "Downloads finished",
            DownloadQueue.BatchSummary(batch[0].Title, done, skipped, failed));
    }

    /// <summary>Downloads that are running or waiting for a free slot.</summary>
    public int ActiveCount => Items.Count(i => i.State is DownloadState.Queued or DownloadState.Downloading);

    [RelayCommand]
    private void StartAll()
    {
        foreach (var item in Items.Where(i => i.State is DownloadState.Ready or DownloadState.Failed or DownloadState.Paused).ToList())
        {
            Enqueue(item);
        }
    }

    [RelayCommand]
    private void PauseAll()
    {
        foreach (var item in Items.Where(i => i.CanPause).ToList())
        {
            item.PauseInternal();
        }
    }

    [RelayCommand]
    private void CancelAll()
    {
        foreach (var item in Items.Where(i => i.CanCancel).ToList())
        {
            item.CancelInternal();
        }
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var item in Items.Where(i => i.IsFinished).ToList())
        {
            Items.Remove(item);
        }
    }

    /// <summary>Removes the item, after asking if that would stop a download that's running. Returns whether it was removed.</summary>
    internal async Task<bool> RemoveAsync(DownloadItemViewModel item)
    {
        if (item.State == DownloadState.Downloading && !await _host.Dialogs.ConfirmAsync("Remove download?",
                $"\"{item.Title}\" is still downloading. Remove it and stop the download? What's downloaded so far stays in its folder.",
                "Remove", "Keep downloading"))
        {
            return false;
        }

        item.CancelInternal();
        Items.Remove(item);
        return true;
    }

    // ---------- keeping unfinished downloads between sessions ----------

    /// <summary>Remembers what's still in the list and not done.</summary>
    public void SaveUnfinished()
    {
        _host.Settings.UnfinishedDownloads = Items
            .Where(i => DownloadQueue.KeepForNextSession(i.State))
            .Select(i => new SavedDownload
            {
                Url = i.Url,
                PresetId = i.Preset.Id,
                CookieId = i.CookieId,
                DownloaderId = i.DownloaderOverride?.Id,
                Destination = i.DestinationEdited && !string.IsNullOrWhiteSpace(i.Destination) ? i.Destination : null,
                Paused = i.State == DownloadState.Paused,
            })
            .ToList();
    }

    /// <summary>
    /// Puts last session's unfinished downloads back in the list. Their info is read by <see cref="ResumeRestored"/>,
    /// once startup has installed or updated the downloader.
    /// </summary>
    public void RestoreUnfinished()
    {
        foreach (var saved in _host.Settings.UnfinishedDownloads)
        {
            var item = new DownloadItemViewModel(this, saved.Url, _host.Settings.FindPreset(saved.PresetId) ?? _host.Settings.DefaultPreset)
            {
                CookieId = saved.CookieId,
                DownloaderOverride = _host.Settings.FindDownloader(saved.DownloaderId),
                Restored = true,
                RestoredPaused = saved.Paused,
                ProgressText = "Waiting for the startup checks…",
                IsIndeterminate = true,
            };
            if (saved.Destination is { } folder)
            {
                item.Destination = folder; // marks it as picked by hand
            }

            Items.Add(item);
            _restored.Add(item);
        }

        RefreshCookieChoices(); // a restored download may use a browser that's no longer installed

        // The saved list stays until the next autosave replaces it with the current list, which includes these.
        if (_restored.Count > 0)
        {
            _host.SetStatus($"Put back {_restored.Count} unfinished download(s) from last time.");
        }
    }

    public void ResumeRestored()
    {
        foreach (var item in _restored.Where(Items.Contains))
        {
            _ = ResolveAsync(item);
        }

        _restored.Clear();
    }

    public void CancelEverything()
    {
        foreach (var item in Items)
        {
            item.CancelInternal();
        }
    }

    // ---------- per-item actions ----------

    internal async Task BrowseDestinationAsync(DownloadItemViewModel item)
    {
        if (await _host.Dialogs.PickFolderAsync("Choose destination folder", item.Destination) is { } path)
        {
            item.Destination = path;
        }
    }

    /// <summary>Opens the item's folder, with the file it downloaded selected if that's still there.</summary>
    internal Task OpenFolderAsync(DownloadItemViewModel item) => item.LastFile is { } file && File.Exists(file)
        ? _host.Dialogs.ShowFileAsync(file)
        : _host.Dialogs.OpenFolderAsync(item.Destination);

    internal async Task CopyAsync(string text, string what)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        await _host.Dialogs.CopyTextAsync(text);
        _host.SetStatus($"Copied {what}: {text}");
    }

    /// <summary>Adds the same URL as a fresh item (e.g. to download it again with another preset or folder).</summary>
    internal void AddAgain(DownloadItemViewModel item)
    {
        var copy = new DownloadItemViewModel(this, item.Url, item.Preset) { CookieId = item.CookieId, DownloaderOverride = item.DownloaderOverride };
        Items.Insert(Items.IndexOf(item) + 1, copy);
        _ = ResolveAsync(copy);
    }

    /// <summary>Turns the item's current folder into a rule, so this channel (or playlist/site) is routed there from now on.</summary>
    internal void CreateRuleFrom(DownloadItemViewModel item)
    {
        if (item.Info is not { } info || string.IsNullOrWhiteSpace(item.Destination))
        {
            return;
        }

        var (field, value) = info.Channel is { } ch ? (MatchField.Channel, ch)
            : info.Playlist is { } pl ? (MatchField.Playlist, pl)
            : (MatchField.Domain, info.Domain ?? "");
        if (value.Length == 0)
        {
            return;
        }

        var rule = new RoutingRule
        {
            Name = $"{field}: {value}",
            Destination = item.Destination,
            PresetId = item.Preset.Id != _host.Settings.DefaultPresetId ? item.Preset.Id : null,
            CookieId = Cookies.IsNone(item.CookieId) ? null : item.CookieId,
            Conditions = [new RuleCondition { Field = field, Operator = MatchOperator.Equals, Value = value }],
        };
        _host.Settings.Rules.Insert(0, rule);

        item.RouteDescription = "Rule: " + rule.Name;
        item.DestinationEdited = false;
        _host.SetStatus($"Added rule \"{rule.Name}\" → {rule.Destination}");
    }

    private void UpdateSummary() => Summary = DownloadQueue.ListSummary(Items.Select(i => i.State).ToList());
}
