using System.Diagnostics;
using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

public sealed class FileAndProcessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vidarchivergui-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ReplaceFile_works_while_the_old_file_is_in_use()
    {
        var target = Path.Combine(_dir, "tool.exe");
        var update = Path.Combine(_dir, "tool.exe.download");
        File.WriteAllText(target, "old");
        File.WriteAllText(update, "new");

        // A running executable is open with read + delete sharing: it can be renamed but not overwritten.
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            ReleaseDownloader.ReplaceFile(update, target);
        }

        Assert.Equal("new", File.ReadAllText(target));
        Assert.False(File.Exists(update));
    }

    [Fact]
    public void ReplaceFile_cleans_up_old_copies()
    {
        var target = Path.Combine(_dir, "tool");
        var update = Path.Combine(_dir, "tool.download");
        File.WriteAllText(target, "old");
        File.WriteAllText(target + ".old", "older");
        File.WriteAllText(update, "new");

        ReleaseDownloader.ReplaceFile(update, target);

        Assert.Equal("new", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".old"));
    }

    [Fact]
    public async Task Cancelling_kills_the_process_immediately()
    {
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("ping", new[] { "-n", "30", "127.0.0.1" })
            : ("sleep", new[] { "30" });
        using var cts = new CancellationTokenSource();
        var run = ProcessHelper.RunAsync(exe, args, cts.Token);
        await Task.Delay(300);

        var sw = Stopwatch.StartNew();
        cts.Cancel(); // the kill happens inside Cancel(), not in a later continuation
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Settings_load_waits_out_a_brief_lock()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new SettingsStore(path);
        var saved = SettingsStore.CreateDefaults();
        saved.MaxConcurrentDownloads = 4;
        store.SaveIfChanged(saved);

        var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _ = Task.Delay(300).ContinueWith(_ => locked.Dispose());

        Assert.Equal(4, store.Load().MaxConcurrentDownloads);
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));
    }

    [Fact]
    public void Settings_are_only_written_when_they_changed()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var settings = SettingsStore.CreateDefaults();

        Assert.True(store.SaveIfChanged(settings));
        Assert.False(store.SaveIfChanged(settings));
        settings.MaxConcurrentDownloads = 3;
        Assert.True(store.SaveIfChanged(settings));
    }

    [Fact]
    public void Loading_saved_settings_does_not_rewrite_them()
    {
        var path = Path.Combine(_dir, "settings.json");
        new SettingsStore(path).SaveIfChanged(new SettingsStore(path).Load());

        var store = new SettingsStore(path);
        Assert.False(store.SaveIfChanged(store.Load()));
    }

    [Fact]
    public void Settings_keep_the_downloader_names_from_before_the_rename()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
            {
              "Presets": [ { "Id": "p1", "Name": "Audio", "EngineId": "yt-dlp-nightly" } ],
              "Engines": [ { "Name": "My fork", "ExecutablePath": "my-fork", "Id": "fork" } ],
              "DefaultEngineId": "fork",
              "UnfinishedDownloads": [ { "Url": "https://example.com/a", "EngineId": "yt-dlp-master" } ]
            }
            """);
        var store = new SettingsStore(path);

        var loaded = store.Load();
        Assert.Equal("yt-dlp-nightly", loaded.Presets[0].DownloaderId);
        Assert.Equal("yt-dlp-master", loaded.UnfinishedDownloads[0].DownloaderId);
        Assert.Equal("fork", loaded.DefaultDownloaderId);

        store.SaveIfChanged(loaded);
        var json = File.ReadAllText(path);
        Assert.Contains("\"Engines\"", json);
        Assert.Contains("\"DefaultEngineId\"", json);
        Assert.Contains("\"EngineId\"", json);
    }

    [Fact]
    public void Unfinished_downloads_survive_a_restart()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var saved = SettingsStore.CreateDefaults();
        saved.NotifyWhenDone = false;
        saved.UnfinishedDownloads =
        [
            new SavedDownload { Url = "https://example.com/a", PresetId = saved.Presets[0].Id, Destination = @"D:\Picked" },
            new SavedDownload { Url = "https://example.com/b" },
        ];
        store.SaveIfChanged(saved);

        var loaded = store.Load();
        Assert.False(loaded.NotifyWhenDone);
        Assert.Equal(saved.UnfinishedDownloads, loaded.UnfinishedDownloads);
    }
}
