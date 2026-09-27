using System.Collections.ObjectModel;
using Avalonia.Threading;
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
    internal AppSettings Settings => _host.Settings;

    /// <summary>
    /// Shared by the Add bar and every item. Updated in place (so the drop-downs keep their items); selections are
    /// stored as ids, which is why the selected-item properties ignore the null a drop-down reports while it's cleared.
    /// </summary>
    public ObservableCollection<CookieChoice> CookieChoices { get; } = [];

    public CookieChoice? SelectedCookie
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

    public bool ShowLog
    {
        get => _host.Settings.ShowLog;
        set
        {
            _host.Settings.ShowLog = value;
            OnPropertyChanged();
        }
    }

    public double LogHeight
    {
        get => _host.Settings.LogHeight;
        set
        {
            _host.Settings.LogHeight = value;
            OnPropertyChanged();
        }
    }

    internal CookieChoice? FindCookieChoice(string? id) =>
        CookieChoices.FirstOrDefault(c => c.Id == (id ?? Cookies.NoneId)) ?? CookieChoices.FirstOrDefault();

    /// <summary>Called when the tab is shown, since browsers may have been installed or cookie sources edited.</summary>
    public void RefreshCookieChoices()
    {
        var choices = Cookies.Choices(_host.Settings, Cookies.DetectBrowsers());
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
        var preset = SelectedPreset ?? _host.Settings.DefaultPreset;
        var added = 0;
        foreach (var url in ExtractUrls(UrlInput))
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
        UrlInput = "";
        if (added == 0)
        {
            _host.SetStatus("No new URLs to add.");
        }
    }

    private static IEnumerable<string> ExtractUrls(string text) =>
        text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().Trim('"', '\'', '<', '>'))
            .Where(s => s.Length > 0 && (s.Contains("://") || s.Contains('.')))
            .Distinct();

    private async Task ResolveAsync(DownloadItemViewModel item)
    {
        var token = item.BeginOperation();
        item.State = DownloadState.Resolving;
        item.Error = null;
        item.IsIndeterminate = true;
        item.ProgressText = "Reading info…";
        try
        {
            await _resolveGate.WaitAsync(token);
            FallbackResult result;
            try
            {
                var presetArgs = ArgumentParser.Split(item.Preset.Arguments);
                result = await Resilience.RunAsync(_host.Tools, _host.Settings, item.Engine,
                    async (engine, ct) =>
                    {
                        try
                        {
                            var cookieArgs = Cookies.Args(_host.Settings, item.CookieId, engine.Flavor);
                            item.Info = await _host.Metadata.FetchAsync(item.Url, presetArgs, engine, cookieArgs, ct);
                            return null;
                        }
                        catch (YtDlpException e)
                        {
                            item.AppendLog("ERROR: " + e.Message);
                            return e.Message;
                        }
                    },
                    message => Dispatcher.UIThread.Post(() => { item.ProgressText = message; item.AppendLog(message); }),
                    token);
            }
            finally
            {
                _resolveGate.Release();
            }

            if (result.Error is not null)
            {
                item.State = DownloadState.Failed;
                item.Error = "Could not read info: " + result.Error + item.AdviceFor(result.Kind);
                return;
            }
            item.EngineOverride = result.Engine == _host.Settings.EngineFor(item.Preset) ? null : result.Engine;

            ApplyRoute(item);
            item.State = DownloadState.Ready;
            item.ProgressText = "";
            if (_host.Settings.AutoStartDownloads)
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

        var route = RoutingEngine.Resolve(item.Info, _host.Settings.Rules, _host.Settings.FallbackDestination);
        item.SetRoutedDestination(route.Destination);
        item.RouteDescription = route.Describe();
        if (_host.Settings.FindPreset(route.PresetId) is { } preset)
        {
            item.Preset = preset;
        }

        if (route.CookieId is not null)
        {
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
            _ = ResolveAsync(item); // metadata failed earlier: retry that first
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
        var running = Items.Count(i => i.State == DownloadState.Downloading);
        foreach (var next in Items.Where(i => i.State == DownloadState.Queued).ToList())
        {
            if (running >= _host.Settings.MaxConcurrentDownloads)
            {
                break;
            }

            running++;
            _ = RunAsync(next);
        }
        UpdateSummary();
    }

    private async Task RunAsync(DownloadItemViewModel item)
    {
        await item.RunAsync(_host.Runner, _host.Tools, _host.Settings);
        if (item.State == DownloadState.Completed)
        {
            _host.SetStatus($"Finished: {item.Title}");
        }

        Pump();
    }

    [RelayCommand]
    private void StartAll()
    {
        foreach (var item in Items.Where(i => i.State == DownloadState.Ready).ToList())
        {
            Enqueue(item);
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

    internal void Remove(DownloadItemViewModel item)
    {
        item.CancelInternal();
        Items.Remove(item);
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

    internal Task OpenFolderAsync(DownloadItemViewModel item) => _host.Dialogs.OpenFolderAsync(item.Destination);

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
        var copy = new DownloadItemViewModel(this, item.Url, item.Preset) { CookieId = item.CookieId, EngineOverride = item.EngineOverride };
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
        _host.Save(quiet: true);

        item.RouteDescription = "Rule: " + rule.Name;
        item.DestinationEdited = false;
        _host.SetStatus($"Added rule \"{rule.Name}\" → {rule.Destination}");
    }

    private void UpdateSummary()
    {
        int Count(DownloadState s) => Items.Count(i => i.State == s);
        Summary = $"{Items.Count} items  ·  {Count(DownloadState.Downloading)} downloading  ·  {Count(DownloadState.Queued)} queued  ·  " +
                  $"{Count(DownloadState.Completed)} done  ·  {Count(DownloadState.Failed)} failed";
    }
}
