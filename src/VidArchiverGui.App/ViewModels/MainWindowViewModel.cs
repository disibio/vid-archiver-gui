using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using VidArchiverGui.App.Services;

namespace VidArchiverGui.App.ViewModels;

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
        Setup.DetailsRequested += () => SelectedTab = SettingsTabIndex;
        Tools = new SettingsViewModel(host, Setup);
        host.StatusChanged += message => Dispatcher.UIThread.Post(() => Status = message);
        Downloads.RestoreUnfinished();
    }

    public DownloadsViewModel Downloads { get; }
    public RulesViewModel Rules { get; }
    public PresetsViewModel Presets { get; }
    public SettingsViewModel Tools { get; }
    public SetupViewModel Setup { get; }

    private const int SettingsTabIndex = 3;
    public AboutViewModel About { get; } = new();

    [ObservableProperty] private string _status = "Ready";

    [ObservableProperty] private int _selectedTab;

    partial void OnSelectedTabChanged(int value)
    {
        if (value == 0)
        {
            Downloads.RefreshCookieChoices();
        }
        if (value == 1)
        {
            Rules.RefreshPresetChoices();
        }
        if (value == 2)
        {
            Presets.RefreshEngineChoices();
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            await Tools.InitializeAsync();
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
