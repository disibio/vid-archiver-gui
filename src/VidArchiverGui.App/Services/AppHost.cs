using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.Services;

/// <summary>The settings, dialogs and status bar, handed to every view model.</summary>
public sealed class AppHost
{
    private readonly SettingsStore _store;

    public AppHost(AppSettings settings, SettingsStore store, IDialogs dialogs)
    {
        Settings = settings;
        _store = store;
        Dialogs = dialogs;
    }

    public AppSettings Settings { get; }
    public IDialogs Dialogs { get; }

    public event Action<string>? StatusChanged;

    public void SetStatus(string message) => StatusChanged?.Invoke(message);

    /// <summary>
    /// Writes the settings if anything changed. Called every few seconds and on exit, so edits never need a Save
    /// button. Returns why the file couldn't be written, or null once it could.
    /// </summary>
    public string? Save()
    {
        try
        {
            _store.SaveIfChanged(Settings);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "Settings aren't being saved: " + e.Message;
        }
    }
}
