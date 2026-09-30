using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.Services;

/// <summary>Shared services and settings handed to every view model.</summary>
public sealed class AppHost
{
    private readonly SettingsStore _store;

    public AppHost(AppSettings settings, SettingsStore store, IDialogs dialogs)
    {
        Settings = settings;
        _store = store;
        Dialogs = dialogs;
        Tools = new ToolManager(settings);
        Metadata = new MetadataService();
        Runner = new DownloadRunner(Tools);
    }

    public AppSettings Settings { get; }
    public IDialogs Dialogs { get; }
    public ToolManager Tools { get; }
    public MetadataService Metadata { get; }
    public DownloadRunner Runner { get; }

    public event Action<string>? StatusChanged;

    public void SetStatus(string message) => StatusChanged?.Invoke(message);

    public void Save(bool quiet = false)
    {
        try
        {
            _store.Save(Settings);
            if (!quiet)
            {
                SetStatus($"Settings saved ({DateTime.Now:t})");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus("Could not save settings: " + e.Message);
        }
    }
}
