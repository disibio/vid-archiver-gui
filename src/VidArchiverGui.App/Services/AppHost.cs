using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.Services;

/// <summary>Shared services and settings handed to every view model.</summary>
public sealed class AppHost
{
    private readonly SettingsStore _store;

    // The last save error shown, so a file that stays locked doesn't repeat it on every autosave.
    private string? _lastSaveError;

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

    /// <summary>Writes the settings if anything changed. Called every few seconds and on exit, so edits never need a Save button.</summary>
    public void Save()
    {
        try
        {
            _store.SaveIfChanged(Settings);
            _lastSaveError = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (e.Message != _lastSaveError)
            {
                _lastSaveError = e.Message;
                SetStatus("Could not save settings: " + e.Message);
            }
        }
    }
}
