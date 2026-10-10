using VidArchiverGui.App.Services;
using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.Tests;

/// <summary>Answers dialogs from a script instead of showing them, and remembers what was asked.</summary>
public sealed class FakeDialogs : IDialogs
{
    /// <summary>What the user "picks" in the next Confirm or Ask dialogs, in order (true, false, or null for Cancel).</summary>
    public Queue<bool?> Answers { get; } = new();

    /// <summary>The file the next Open or Save dialog returns; null = cancelled.</summary>
    public string? FileToPick { get; set; }

    /// <summary>The messages of the Confirm and Ask dialogs shown so far.</summary>
    public List<string> Questions { get; } = [];

    public bool IsWindowActive { get; set; } = true;

    public Task<bool> ConfirmAsync(string title, string message, string yes, string no) =>
        Task.FromResult(Answer(message) == true);

    public Task<bool?> AskAsync(string title, string message, string yes, string no) => Task.FromResult(Answer(message));

    private bool? Answer(string message)
    {
        Questions.Add(message);
        return Answers.Count > 0 ? Answers.Dequeue() : throw new InvalidOperationException("Unexpected question: " + message);
    }

    public Task<string?> PickJsonFileAsync(string title) => Task.FromResult(FileToPick);
    public Task<string?> SaveJsonFileAsync(string title, string suggestedName) => Task.FromResult(FileToPick);
    public Task<string?> PickFolderAsync(string title, string? startPath = null) => Task.FromResult<string?>(null);
    public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    public Task OpenFolderAsync(string path)
    {
        Opened.Add(path);
        return Task.CompletedTask;
    }

    public Task ShowFileAsync(string file)
    {
        Opened.Add(file);
        return Task.CompletedTask;
    }

    /// <summary>The folders and files opened in the file manager so far.</summary>
    public List<string> Opened { get; } = [];
    public Task CopyTextAsync(string text) => Task.CompletedTask;

    /// <summary>The desktop notifications shown so far, as "title: message".</summary>
    public List<string> Notifications { get; } = [];

    public void Notify(string title, string message) => Notifications.Add($"{title}: {message}");
}

/// <summary>An <see cref="AppHost"/> over fresh default settings, with a scratch folder for import and export files.</summary>
public sealed class TestHost : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vidarchivergui-apptests-" + Guid.NewGuid().ToString("N"));

    public TestHost()
    {
        Directory.CreateDirectory(_dir);
        Settings = SettingsStore.CreateDefaults();
        Settings.AutoStartDownloads = false; // the tests start downloads themselves
        foreach (var downloader in Downloader.CreateBuiltIns())
        {
            Settings.Downloaders.Add(downloader);
        }

        Host = new AppHost(Settings, new SettingsStore(Path.Combine(_dir, "settings.json")), Dialogs);
        Host.StatusChanged += Statuses.Add;
    }

    public AppSettings Settings { get; }
    public FakeDialogs Dialogs { get; } = new();
    public AppHost Host { get; }
    public List<string> Statuses { get; } = [];

    /// <summary>Writes <paramref name="json"/> to a file and makes it the one the next file dialog picks.</summary>
    public void PickFile(string json)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        Dialogs.FileToPick = path;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
