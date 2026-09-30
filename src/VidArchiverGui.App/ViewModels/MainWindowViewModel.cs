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

    public MainWindowViewModel(AppHost host)
    {
        _host = host;
        Downloads = new DownloadsViewModel(host);
        Rules = new RulesViewModel(host);
        Presets = new PresetsViewModel(host);
        Setup = new SetupViewModel(host);
        Setup.DetailsRequested += () => CurrentTab = MainTab.Settings;
        Settings = new SettingsViewModel(host, Setup);
        host.StatusChanged += message => Dispatcher.UIThread.Post(() => Status = message);
        Downloads.RestoreUnfinished();
    }

    public DownloadsViewModel Downloads { get; }
    public RulesViewModel Rules { get; }
    public PresetsViewModel Presets { get; }
    public SettingsViewModel Settings { get; }
    public SetupViewModel Setup { get; }
    public AboutViewModel About { get; } = new();

    [ObservableProperty] private string _status = "Ready";

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
                Rules.RefreshPresetChoices();
                break;
            case MainTab.Presets:
                Presets.RefreshEngineChoices();
                break;
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            await Settings.InitializeAsync();
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
        _host.Save(quiet: true);
    }
}
