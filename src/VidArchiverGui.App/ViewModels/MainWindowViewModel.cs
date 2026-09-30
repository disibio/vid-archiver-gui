using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using VidArchiverGui.App.Services;

namespace VidArchiverGui.App.ViewModels;

/// <summary>The main window's tabs, in the order MainWindow.axaml lists them.</summary>
public enum MainTab
{
    Downloads,
    Rules,
    Presets,
    Settings,
    About,
}

public partial class MainWindowViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindowViewModel(AppHost host)
    {
        _host = host;
        Downloads = new DownloadsViewModel(host);
        Rules = new RulesViewModel(host);
        Presets = new PresetsViewModel(host);
        SettingsTab = new SettingsViewModel(host);
        Setup.DetailsRequested += () => CurrentTab = MainTab.Settings;
        host.StatusChanged += message => Dispatcher.UIThread.Post(() => Status = message);
        Downloads.RestoreUnfinished();

        // Edits are kept without a Save button: the settings are written whenever they've changed.
        _autosave.Tick += (_, _) => SaveError = host.Save();
        _autosave.Start();
    }

    public DownloadsViewModel Downloads { get; }
    public RulesViewModel Rules { get; }
    public PresetsViewModel Presets { get; }
    /// <summary>The Settings tab (named so it isn't mistaken for <see cref="Core.Models.AppSettings"/>).</summary>
    public SettingsViewModel SettingsTab { get; }
    public SetupViewModel Setup => SettingsTab.Setup;
    public AboutViewModel About { get; } = new();

    [ObservableProperty] private string _status = "Ready";

    /// <summary>Why the last autosave failed; shown beside the status until a save works again.</summary>
    [ObservableProperty] private string? _saveError;

    /// <summary>The TabControl's SelectedIndex; see <see cref="CurrentTab"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTab))]
    private int _selectedTab;

    public MainTab CurrentTab
    {
        get => (MainTab)SelectedTab;
        set => SelectedTab = (int)value;
    }

    partial void OnSelectedTabChanged(int value)
    {
        // Refresh what may have changed on other tabs meanwhile.
        switch ((MainTab)value)
        {
            case MainTab.Downloads:
                Downloads.RefreshCookieChoices();
                break;
            case MainTab.Rules:
                Rules.RefreshChoices();
                break;
            case MainTab.Presets:
                Presets.RefreshDownloaderChoices();
                break;
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            await SettingsTab.InitializeAsync();
        }
        finally
        {
            Downloads.ResumeRestored();
        }
    }

    public void Shutdown()
    {
        Downloads.SaveUnfinished();
        Downloads.CancelEverything();
        _autosave.Stop();
        _host.Save();
    }
}
