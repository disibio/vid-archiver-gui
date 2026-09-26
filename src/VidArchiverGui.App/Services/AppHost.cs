using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.Services;

/// <summary>Shared services and settings handed to every view model.</summary>
public sealed class AppHost
{
    public AppHost(AppSettings settings, SettingsStore store, IDialogs dialogs)
    {
        Settings = settings;
        Store = store;
        Dialogs = dialogs;
        Tools = new ToolManager(settings);
        Metadata = new MetadataService();
        Runner = new DownloadRunner(Tools);
    }

    public AppSettings Settings { get; }
    public SettingsStore Store { get; }
    public IDialogs Dialogs { get; }
    public ToolManager Tools { get; }
    public MetadataService Metadata { get; }
    public DownloadRunner Runner { get; }

    public event Action<string>? StatusChanged;

    public void SetStatus(string message) => StatusChanged?.Invoke(message);

    public bool Save(bool quiet = false)
    {
        try
        {
            Store.Save(Settings);
            if (!quiet)
            {
                SetStatus($"Settings saved ({DateTime.Now:t})");
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus("Could not save settings: " + e.Message);
            return false;
        }
    }
}
