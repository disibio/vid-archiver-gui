using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VidArchiverGui.App.Services;
using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.ViewModels;

/// <summary>
/// The banner saying a newer version of the app is out, with a link to its release page. The check can be turned off
/// in Settings or from the banner, and never runs on builds that something else updates (see <see cref="AppUpdate"/>).
/// </summary>
public partial class AppUpdateViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly string _currentVersion;
    private readonly bool _updatedElsewhere;
    private readonly Func<CancellationToken, Task<string?>> _latestTag;

    /// <param name="latestTag">Asks for the newest release's tag; see <see cref="AppUpdate.GetLatestTagAsync"/>.</param>
    public AppUpdateViewModel(AppHost host, string currentVersion, bool updatedElsewhere, Func<CancellationToken, Task<string?>> latestTag)
    {
        _host = host;
        _currentVersion = currentVersion;
        _updatedElsewhere = updatedElsewhere;
        _latestTag = latestTag;
        host.Settings.PropertyChanged += OnSettingChanged;
    }

    /// <summary>The newer version found, e.g. "1.2.0"; null when there's nothing to tell.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBanner), nameof(BannerText), nameof(ReleaseUrl))]
    private string? _availableVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBanner))]
    private bool _dismissed;

    public bool ShowBanner => AvailableVersion is not null && !Dismissed && _host.Settings.CheckForAppUpdates;

    public string BannerText => $"Vid Archiver GUI {AvailableVersion} is available (you have {_currentVersion}).";

    public string ReleaseUrl => AvailableVersion is null ? "" : AppUpdate.ReleasePage(AvailableVersion);

    /// <summary>Asks GitHub for the latest release if the check is on and hasn't run in the last day. Failures are silent.</summary>
    public async Task CheckIfDueAsync(CancellationToken ct = default)
    {
        var settings = _host.Settings;
        if (!AppUpdate.IsDue(settings.CheckForAppUpdates, _updatedElsewhere, settings.LastAppUpdateCheck, DateTimeOffset.Now))
        {
            return;
        }

        string? tag;
        try
        {
            tag = await _latestTag(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return; // offline or GitHub unavailable: try again next start
        }

        settings.LastAppUpdateCheck = DateTimeOffset.Now;
        if (settings.CheckForAppUpdates) // it may have been turned off while waiting
        {
            AvailableVersion = AppUpdate.NewerVersion(tag, _currentVersion, settings.SkippedAppVersion);
        }
    }

    /// <summary>Hides the banner until next start.</summary>
    [RelayCommand]
    private void Dismiss() => Dismissed = true;

    /// <summary>Doesn't mention this version again; a later one is still offered.</summary>
    [RelayCommand]
    private void SkipVersion()
    {
        _host.Settings.SkippedAppVersion = AvailableVersion;
        AvailableVersion = null;
    }

    /// <summary>Turns the check off (the same as clearing the box in Settings).</summary>
    [RelayCommand]
    private void StopChecking()
    {
        _host.Settings.CheckForAppUpdates = false;
        _host.SetStatus("Won't check for new versions of the app. You can turn this back on in Settings.");
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.CheckForAppUpdates))
        {
            OnPropertyChanged(nameof(ShowBanner));
        }
    }
}
