using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using VidArchiverGui.App.Services;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.ViewModels;

public partial class SetupItemViewModel(SetupItem item, SetupViewModel owner, string section) : ObservableObject
{
    public SetupItem Item => item;
    public string Section => section;
    public string Name => item.Name;
    public string Why => item.Why;
    public string? Command => item.Command;
    public bool HasCommand => item.Command is not null;
    public bool IsOk => item.Status == SetupStatus.Ok;
    public bool IsWarning => item.Status == SetupStatus.Warning;
    public bool IsMissing => item.Status == SetupStatus.Missing;
    public bool IsChecking => item.Status == SetupStatus.Checking;
    public bool ShowWhy => !IsOk && !IsChecking;

    /// <summary>The result, or while checking: what the check is doing right now and for how long.</summary>
    [ObservableProperty] private string _detail = item.Detail;

    /// <summary>What a still-checking row is waiting on, and since when (for the seconds counter).</summary>
    public string Activity { get; private set; } = item.Detail;
    private DateTime _activitySince = DateTime.UtcNow;

    public void SetActivity(string activity)
    {
        Activity = activity;
        _activitySince = DateTime.UtcNow;
        Tick();
    }

    public void Tick()
    {
        var seconds = (int)(DateTime.UtcNow - _activitySince).TotalSeconds;
        Detail = seconds < 2 ? Activity : $"{Activity} {seconds} s";
    }
    public bool HasAction => item.Fix != SetupFix.None && item.Status != SetupStatus.Ok
        && (item.Fix != SetupFix.MissingFolder || item.CanCreateFolder);

    public string ActionText => item.Fix switch
    {
        SetupFix.InstallDownloader or SetupFix.InstallDeno or SetupFix.InstallFfmpeg => "Install",
        SetupFix.MissingFolder => "Create folder",
        SetupFix.CopyCommand => "Copy command",
        _ => "",
    };

    [RelayCommand] private Task Fix() => owner.FixAsync(this);

    /// <summary>A missing folder can also be swapped for one the user picks.</summary>
    public bool CanChooseFolder => item.Fix == SetupFix.MissingFolder && item.Status != SetupStatus.Ok;

    [RelayCommand] private Task ChooseFolder() => owner.ChooseFolderAsync(this);
}

/// <summary>The setup checklist: what downloads need on this machine, with one-click fixes where possible.</summary>
public partial class SetupViewModel(AppHost host) : ObservableObject
{
    private readonly SetupChecker _checker = new(host.Settings, host.Tools);
    private readonly SemaphoreSlim _gate = new(1);

    public ObservableCollection<SetupItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    /// <summary>What the current fix is doing (e.g. download progress), with a Cancel button.</summary>
    public BusyStatusViewModel Busy { get; } = new();
    [ObservableProperty] private string _message = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBanner))]
    private bool _bannerDismissed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBanner))]
    private string _bannerText = "";

    [ObservableProperty] private bool _canAutoFix;

    public bool IsIdle => !IsBusy;
    public bool ShowBanner => BannerText.Length > 0 && !BannerDismissed;

    /// <summary>Raised after a fix installed something, so other views can refresh their tool status.</summary>
    public event Action? ToolsChanged;

    /// <summary>Raised when the banner's "Details" is clicked.</summary>
    public event Action? DetailsRequested;

    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _tickerHooked;

    /// <summary>
    /// Before a check can run (e.g. while the downloader is still being installed), show the checklist's rows and what
    /// they're waiting for, instead of an empty list.
    /// </summary>
    public void ShowWaiting(string what)
    {
        if (Items.Count == 0)
        {
            ShowPlan();
        }

        foreach (var row in Items.Where(r => r.IsChecking))
        {
            row.SetActivity("Waiting: " + char.ToLowerInvariant(what[0]) + what[1..]);
        }
    }

    private void ShowPlan()
    {
        Items.Clear();
        foreach (var (section, name) in _checker.Plan())
        {
            Items.Add(new SetupItemViewModel(new SetupItem(name, SetupStatus.Checking, "Waiting to be checked…", ""), this, section));
        }

        StartTicker();
    }

    private void StartTicker()
    {
        if (!_tickerHooked)
        {
            _ticker.Tick += (_, _) =>
            {
                var checking = Items.Where(r => r.IsChecking).ToList();
                checking.ForEach(r => r.Tick());
                if (checking.Count == 0)
                {
                    _ticker.Stop();
                }
            };
            _tickerHooked = true;
        }

        _ticker.Start();
    }

    [RelayCommand]
    public async Task CheckAsync()
    {
        await _gate.WaitAsync();
        try
        {
            ShowPlan();
            await _checker.CheckAsync(
                activity: (section, what) =>
                {
                    foreach (var row in Items.Where(r => r.Section == section && r.IsChecking))
                    {
                        row.SetActivity(what);
                    }
                },
                done: (section, results) =>
                {
                    var at = Items.ToList().FindIndex(r => r.Section == section);
                    foreach (var old in Items.Where(r => r.Section == section).ToList())
                    {
                        Items.Remove(old);
                    }

                    at = at < 0 ? Items.Count : Math.Min(at, Items.Count);
                    foreach (var r in results)
                    {
                        Items.Insert(at++, new SetupItemViewModel(r, this, section));
                    }
                });

            UpdateBanner();
        }
        catch (Exception e)
        {
            Message = "Setup check failed: " + e.Message;
            foreach (var row in Items.Where(r => r.IsChecking))
            {
                row.Detail = $"Not checked: the setup check failed while this was \"{row.Activity}\"";
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void UpdateBanner()
    {
        var problems = Items.Where(i => !i.IsOk && !i.IsChecking).ToList();
        CanAutoFix = problems.Any(p => p.Item.CanAutoFix);
        if (problems.Count == 0)
        {
            BannerText = "";
            return;
        }
        var names = string.Join(", ", problems.Select(p => p.Name));
        BannerText = problems.Any(p => p.IsMissing)
            ? $"Setup needed: {names}. Some downloads won't work until this is fixed."
            : $"Setup check: {names} need attention.";
    }

    [RelayCommand]
    private async Task FixAll()
    {
        foreach (var item in Items.Where(i => !i.IsOk && i.Item.CanAutoFix).ToList())
        {
            if (!await RunFixAsync(item))
            {
                break;
            }
        }

        await CheckAsync();
    }

    public async Task FixAsync(SetupItemViewModel item)
    {
        if (item.Item.Fix == SetupFix.CopyCommand)
        {
            await host.Dialogs.CopyTextAsync(item.Command!);
            Message = "Copied: " + item.Command;
            return;
        }
        if (await RunFixAsync(item))
        {
            await CheckAsync();
        }
    }

    private async Task<bool> RunFixAsync(SetupItemViewModel item)
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        Message = "";
        var ct = Busy.Start(item.Item.Fix switch
        {
            SetupFix.InstallFfmpeg => "Installing ffmpeg (≈100 MB download)",
            SetupFix.MissingFolder => $"Creating {item.Item.Folder}",
            _ => $"Installing {item.Name}",
        }, host.SetStatus);
        try
        {
            var progress = Busy.Progress;
            switch (item.Item.Fix)
            {
                case SetupFix.InstallDownloader:
                    await host.Tools.InstallAsync(host.Settings.DefaultEngine, progress, ct);
                    break;
                case SetupFix.InstallDeno:
                    await host.Tools.InstallDenoAsync(progress, ct);
                    break;
                case SetupFix.InstallFfmpeg:
                    await host.Tools.DownloadFfmpegAsync(progress, ct);
                    break;
                case SetupFix.MissingFolder:
                    Directory.CreateDirectory(item.Item.Folder!);
                    break;
            }
            Message = $"{item.Name}: done.";
            host.SetStatus(Message);
            ToolsChanged?.Invoke();
            return true;
        }
        catch (OperationCanceledException) when (Busy.WasCancelled)
        {
            Message = $"{item.Name}: cancelled.";
            host.SetStatus(Message);
            return false;
        }
        catch (Exception e)
        {
            Message = $"{item.Name}: {e.Message}";
            host.SetStatus(Message);
            return false;
        }
        finally
        {
            Busy.Stop();
            IsBusy = false;
        }
    }

    /// <summary>Instead of creating a missing archive folder, point the presets that use it at another folder.</summary>
    public async Task ChooseFolderAsync(SetupItemViewModel item)
    {
        if (IsBusy || item.Item.Folder is not { } oldFolder
            || await host.Dialogs.PickFolderAsync("Choose where to keep the download archive", oldFolder) is not { } newFolder)
        {
            return;
        }

        var changed = SetupChecker.MoveArchiveFolder(host.Settings.Presets, oldFolder, newFolder);
        if (changed.Count == 0)
        {
            Message = $"{item.Name}: couldn't update the preset's --download-archive path. Edit it on the Presets tab.";
            return;
        }

        host.Save(quiet: true);
        Message = $"Archive folder changed to {newFolder} for {string.Join(", ", changed.Select(p => p.Name))}.";
        host.SetStatus(Message);
        await CheckAsync();
    }

    [RelayCommand] private void DismissBanner() => BannerDismissed = true;

    [RelayCommand] private void ShowDetails() => DetailsRequested?.Invoke();
}
