using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

/// <summary>Loading and saving settings, and keeping their cross-references valid.</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vidarchivergui-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

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

    [Fact]
    public void Settings_load_adds_builtins_and_keeps_custom_downloaders()
    {
        var file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, """
            { "Presets": [ { "Id": "p1", "Name": "x", "Arguments": "", "EngineId": "gone" } ],
              "Engines": [ { "Id": "yt-dlp", "Name": "tampered", "GitHubRepo": "evil/repo" },
                           { "Id": "fork", "Name": "Fork", "ExecutablePath": "C:\\tools\\yt-dlp-fork.exe" } ] }
            """);
        var s = new SettingsStore(file).Load();

        Assert.Equal(["yt-dlp", "yt-dlp-nightly", "yt-dlp-master", "youtube-dl"], s.Downloaders.Take(4).Select(e => e.Id));
        Assert.Equal("yt-dlp/yt-dlp", s.FindDownloader("yt-dlp")!.GitHubRepo); // built-in definitions come from code
        var custom = Assert.Single(s.Downloaders, e => !e.IsManaged);
        Assert.Equal(@"C:\tools\yt-dlp-fork.exe", custom.ExecutablePath);
        Assert.Equal("fork", custom.Id);
        Assert.Null(s.Presets[0].DownloaderId); // pointed at a downloader that no longer exists
    }

    [Fact]
    public void Settings_load_drops_references_to_removed_sources()
    {
        var file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, """
            { "LastCookieId": "deleted",
              "CookieSources": [ { "Id": "kept", "Name": "k", "Kind": "File", "Value": "c.txt" } ],
              "Rules": [ { "Name": "a", "CookieId": "deleted" }, { "Name": "b", "CookieId": "kept" },
                         { "Name": "c", "CookieId": "browser:firefox" }, { "Name": "d", "CookieId": "none" } ] }
            """);
        var s = new SettingsStore(file).Load();

        Assert.Null(s.LastCookieId);
        Assert.Equal([null, "kept", "browser:firefox", "none"], s.Rules.Select(r => r.CookieId));
    }

    [Fact]
    public void Preset_downloader_falls_back_to_default()
    {
        var s = SettingsStore.CreateDefaults(); // downloaders are only filled in by Load(), so add them here
        foreach (var e in Downloader.CreateBuiltIns())
        {
            s.Downloaders.Add(e);
        }

        var preset = s.Presets[0];
        Assert.Equal(Downloader.StableId, s.DownloaderFor(preset).Id);
        preset.DownloaderId = "youtube-dl";
        Assert.Equal(DownloaderFlavor.YoutubeDl, s.DownloaderFor(preset).Flavor);
    }

    [Fact]
    public void Removing_things_drops_the_choices_that_pointed_at_them()
    {
        var s = SettingsStore.CreateDefaults();
        var custom = new Downloader { Name = "Fork", ExecutablePath = "fork" };
        var cookies = new CookieSource { Name = "Work" };
        s.Downloaders = [.. Downloader.CreateBuiltIns(), custom];
        s.CookieSources.Add(cookies);
        s.DefaultDownloaderId = custom.Id;
        s.Presets[1].DownloaderId = custom.Id;
        s.LastCookieId = cookies.Id;
        var deletedPreset = s.Presets[0]; // the default
        var rule = new RoutingRule { PresetId = deletedPreset.Id, CookieId = cookies.Id };
        var browserRule = new RoutingRule { PresetId = s.Presets[1].Id, CookieId = Cookies.BrowserId("firefox") };
        s.Rules = [rule, browserRule];

        s.Downloaders.Remove(custom);
        s.CookieSources.Remove(cookies);
        s.Presets.Remove(deletedPreset);
        s.RemoveDanglingReferences();

        Assert.Equal(Downloader.StableId, s.DefaultDownloaderId);
        Assert.Null(s.Presets[0].DownloaderId);
        Assert.Equal(s.Presets[0].Id, s.DefaultPresetId);
        Assert.Null(s.LastCookieId);
        Assert.Equal((null, null), (rule.PresetId, rule.CookieId));
        Assert.Equal((s.Presets[0].Id, Cookies.BrowserId("firefox")), (browserRule.PresetId, browserRule.CookieId));
    }
}
