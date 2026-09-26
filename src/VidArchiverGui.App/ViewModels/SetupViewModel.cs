using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VidArchiverGui.App.Services;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.ViewModels;

public partial class SetupItemViewModel(SetupItem item, SetupViewModel owner) : ObservableObject
{
    public SetupItem Item => item;
    public string Name => item.Name;
    public string Detail => item.Detail;
    public string Why => item.Why;
    public string? Command => item.Command;
    public bool HasCommand => item.Command is not null;
    public bool IsOk => item.Status == SetupStatus.Ok;
    public bool IsWarning => item.Status == SetupStatus.Warning;
    public bool IsMissing => item.Status == SetupStatus.Missing;
    public bool HasAction => item.Fix != SetupFix.None && item.Status != SetupStatus.Ok;

    public string ActionText => item.Fix switch
    {
        SetupFix.InstallDownloader or SetupFix.InstallDeno or SetupFix.InstallFfmpeg => "Install",
        SetupFix.CreateFolder => "Create folder",
        SetupFix.CopyCommand => "Copy command",
        _ => "",
    };

    [RelayCommand] private Task Fix() => owner.FixAsync(this);
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

    [ObservableProperty] private double _progress;
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

    [RelayCommand]
    public async Task CheckAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var results = await _checker.CheckAsync();
            Items.Clear();
            foreach (var r in results)
            {
                Items.Add(new SetupItemViewModel(r, this));
            }

            UpdateBanner();
        }
        catch (Exception e)
        {
            Message = "Setup check failed: " + e.Message;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void UpdateBanner()
    {
        var problems = Items.Where(i => !i.IsOk).ToList();
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
        Progress = 0;
        var progress = new Progress<double>(p => Progress = p * 100);
        Message = item.Item.Fix switch
        {
            SetupFix.InstallFfmpeg => "Installing ffmpeg (≈100 MB download)…",
            SetupFix.CreateFolder => $"Creating {item.Item.Folder}…",
            _ => $"Installing {item.Name}…",
        };
        host.SetStatus(Message);
        try
        {
            switch (item.Item.Fix)
            {
                case SetupFix.InstallDownloader:
                    await host.Tools.InstallAsync(host.Settings.DefaultEngine, progress);
                    break;
                case SetupFix.InstallDeno:
                    await host.Tools.InstallDenoAsync(progress);
                    break;
                case SetupFix.InstallFfmpeg:
                    await host.Tools.DownloadFfmpegAsync(progress);
                    break;
                case SetupFix.CreateFolder:
                    Directory.CreateDirectory(item.Item.Folder!);
                    break;
            }
            Message = $"{item.Name}: done.";
            host.SetStatus(Message);
            ToolsChanged?.Invoke();
            return true;
        }
        catch (Exception e)
        {
            Message = $"{item.Name}: {e.Message}";
            host.SetStatus(Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand] private void DismissBanner() => BannerDismissed = true;

    [RelayCommand] private void ShowDetails() => DetailsRequested?.Invoke();
}
