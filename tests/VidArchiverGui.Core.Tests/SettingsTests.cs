using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

/// <summary>Loading and saving settings, and keeping their cross-references valid.</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vidarchivergui-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Only_one_store_at_a_time_can_lock_the_same_settings()
    {
        var path = Path.Combine(_dir, "settings.json");
        var first = new SettingsStore(path).TryLock();
        Assert.NotNull(first);
        Assert.Null(new SettingsStore(path).TryLock());
        using (var other = new SettingsStore(Path.Combine(_dir, "other", "settings.json")).TryLock())
        {
            Assert.NotNull(other); // another data folder, such as a second portable copy
        }

        first.Dispose();
        using var again = new SettingsStore(path).TryLock();
        Assert.NotNull(again);
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
        // Its own thread rather than a thread-pool continuation: on a busy CI machine, running tests in parallel, the
        // pool can take longer to get to it than Load keeps retrying.
        var unlocker = new Thread(() =>
        {
            Thread.Sleep(250);
            locked.Dispose();
        });
        unlocker.Start();

        try
        {
            Assert.Equal(4, store.Load().MaxConcurrentDownloads);
            Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));
        }
        finally
        {
            unlocker.Join(); // unlocked before Dispose deletes the folder
        }
    }

    [Fact]
    public void An_unreadable_settings_file_is_kept_and_the_defaults_are_used()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        var store = new SettingsStore(path);

        Assert.Equal(2, store.Load().MaxConcurrentDownloads);
        Assert.Equal("{ not json", File.ReadAllText(store.CorruptCopy!));
    }

    [Fact]
    public void Settings_saved_before_a_setting_existed_get_its_default()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ \"MaxConcurrentDownloads\": 3 }");

        var settings = new SettingsStore(path).Load();
        Assert.True(settings.ConfirmCancelAll);
        Assert.True(settings.AutoStartDownloads);
    }

    [Fact]
    public void Data_is_kept_beside_the_app_only_with_portable_txt()
    {
        Assert.Null(AppPaths.PortableDataDir(_dir));

        File.WriteAllText(Path.Combine(_dir, "portable.txt"), "");
        Assert.Equal(Path.Combine(_dir, "data"), AppPaths.PortableDataDir(_dir));
        Assert.True(Directory.Exists(Path.Combine(_dir, "data")));
    }

    [Fact]
    public void Mkv_presets_repackage_single_file_videos_so_the_thumbnail_can_be_embedded()
    {
        foreach (var preset in SettingsStore.CreateDefaults().Presets.Where(p => p.Arguments.Contains("--merge-output-format mkv")))
        {
            var args = ArgumentParser.Split(preset.Arguments);
            Assert.Equal("mkv", args[args.IndexOf("--remux-video") + 1]);
            Assert.Equal("VideoRemuxer+ffmpeg_i:-fflags +genpts", args[args.IndexOf("--postprocessor-args") + 1]);
            Assert.DoesNotContain(args, a => a.StartsWith('#'));
        }
    }

    [Fact]
    public void The_archive_preset_keeps_subtitle_files_after_embedding_them()
    {
        var args = ArgumentParser.Split(SettingsStore.CreateDefaults().Presets[0].Arguments);
        Assert.Contains("--embed-subs", args);
        Assert.Contains("--write-subs", args);
    }

    [Fact]
    public void The_subtitles_preset_downloads_only_uploaded_subtitles_even_for_archived_videos()
    {
        var preset = Assert.Single(SettingsStore.CreateDefaults().Presets, p => p.Name.StartsWith("Uploaded subtitles only"));
        var args = ArgumentParser.Split(preset.Arguments);
        Assert.Contains("--skip-download", args);
        Assert.Contains("--write-subs", args);
        Assert.Equal("all,-live_chat", args[args.IndexOf("--sub-langs") + 1]);
        Assert.DoesNotContain("--write-auto-sub", args);
        Assert.DoesNotContain("--download-archive", args);
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
    public void Unfinished_downloads_survive_a_restart()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var saved = SettingsStore.CreateDefaults();
        saved.NotifyWhenDone = true;
        saved.UnfinishedDownloads =
        [
            new SavedDownload { Url = "https://example.com/a", PresetId = saved.Presets[0].Id, Destination = @"D:\Picked" },
            new SavedDownload { Url = "https://example.com/b" },
        ];
        store.SaveIfChanged(saved);

        var loaded = store.Load();
        Assert.True(loaded.NotifyWhenDone);
        Assert.Equal(saved.UnfinishedDownloads, loaded.UnfinishedDownloads);
    }

    [Fact]
    public void Settings_load_adds_builtins_and_keeps_custom_downloaders()
    {
        var file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, """
            { "Presets": [ { "Id": "p1", "Name": "x", "Arguments": "", "DownloaderId": "gone" } ],
              "Downloaders": [ { "Id": "yt-dlp", "Name": "tampered", "GitHubRepo": "evil/repo" },
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
