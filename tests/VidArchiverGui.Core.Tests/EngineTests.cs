using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

public class EngineTests
{
    private static readonly string Dest = Path.Combine(Path.GetTempPath(), "dest");

    private static List<string> YoutubeDlArgs(params string[] preset) =>
        DownloadRunner.BuildArguments(new DownloadRequest("https://x", preset, Dest, EngineFlavor.YoutubeDl), null);

    [Fact]
    public void YoutubeDl_gets_output_template_instead_of_P()
    {
        var args = YoutubeDlArgs("-f", "best");
        Assert.DoesNotContain("-P", args);
        Assert.DoesNotContain("--progress-template", args);
        Assert.Equal(Path.Combine(Dest, "%(title)s-%(id)s.%(ext)s"), args[args.IndexOf("-o") + 1]);
    }

    [Fact]
    public void YoutubeDl_relative_output_template_is_placed_in_destination()
    {
        var args = YoutubeDlArgs("-o", "%(uploader)s/%(title)s.%(ext)s");
        Assert.Single(args, a => a == "-o");
        Assert.Equal(Path.Combine(Dest, "%(uploader)s/%(title)s.%(ext)s"), args[args.IndexOf("-o") + 1]);
    }

    [Fact]
    public void YoutubeDl_equals_form_and_absolute_templates()
    {
        Assert.Contains("--output=" + Path.Combine(Dest, "%(id)s.%(ext)s"), YoutubeDlArgs("--output=%(id)s.%(ext)s"));

        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "%(id)s.%(ext)s");
        Assert.Contains(absolute, YoutubeDlArgs("-o", absolute));
    }

    [Fact]
    public void YoutubeDl_escapes_percent_in_destination()
    {
        var args = DownloadRunner.BuildArguments(new DownloadRequest("u", [], Path.Combine(Dest, "100% legit"), EngineFlavor.YoutubeDl), null);
        Assert.StartsWith(Path.Combine(Dest, "100%% legit"), args[args.IndexOf("-o") + 1]);
    }

    [Theory]
    [InlineData("[download]  45.3% of 10.00MiB at  1.00MiB/s ETA 00:05", 0.453, 10 * 1024 * 1024.0, 1024 * 1024.0, 5.0)]
    [InlineData("[download]   5.0% of ~ 2.00GiB at 512.00KiB/s ETA 1:02:03", 0.05, 2.0 * 1024 * 1024 * 1024, 512 * 1024.0, 3723.0)]
    [InlineData("[download] 100% of 3.50MiB in 00:03", 1.0, 3.5 * 1024 * 1024, null, null)]
    [InlineData("[download]  12.0% of 1.00MB at Unknown speed ETA Unknown ETA", 0.12, 1_000_000.0, null, null)]
    public void Parses_standard_progress_lines(string line, double fraction, double total, double? speed, double? eta)
    {
        var p = Assert.IsType<OutputEvent.Progress>(YtDlpOutputParser.Parse(line)).Value;
        Assert.Equal(fraction, p.Fraction!.Value, 3);
        Assert.Equal(total, p.TotalBytes!.Value, 0);
        Assert.Equal(speed, p.Speed);
        Assert.Equal(eta, p.Eta);
    }

    [Fact]
    public void Finds_checksums_in_both_list_formats()
    {
        const string sums = """
            1fa6733c37ea6fb51c99ad8fe785e7b7e5f3246c9b980230329d4fb72ed8d4d6  yt-dlp
            66674953FE251B89F4D08C5F0E35E0728679BD67AB3D7D05C0562AF101DD3E7A  yt-dlp.exe
            0823edc54e49e5b2aff1762c745c8dae13d8ba93d6977926a071b6f999f61537 *ffmpeg-master-latest-win64-gpl.zip
            """;
        Assert.Equal("66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a", ReleaseDownloader.FindChecksum(sums, "yt-dlp.exe"));
        Assert.Equal("1fa6733c37ea6fb51c99ad8fe785e7b7e5f3246c9b980230329d4fb72ed8d4d6", ReleaseDownloader.FindChecksum(sums, "yt-dlp"));
        Assert.NotNull(ReleaseDownloader.FindChecksum(sums, "ffmpeg-master-latest-win64-gpl.zip"));
        Assert.Null(ReleaseDownloader.FindChecksum(sums, "yt-dlp_macos"));
    }

    [Fact]
    public void YoutubeDl_archive_message_is_recognised()
    {
        Assert.IsType<OutputEvent.AlreadyDone>(YtDlpOutputParser.Parse("[download] abc has already been recorded in archive"));
    }

    [Fact]
    public void Settings_load_adds_builtins_and_keeps_custom_downloaders()
    {
        var file = Path.Combine(Path.GetTempPath(), $"vidarchivergui-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, """
                { "Presets": [ { "Id": "p1", "Name": "x", "Arguments": "", "EngineId": "gone" } ],
                  "Engines": [ { "Id": "yt-dlp", "Name": "tampered", "GitHubRepo": "evil/repo" },
                               { "Id": "fork", "Name": "Fork", "ExecutablePath": "C:\\tools\\yt-dlp-fork.exe" } ] }
                """);
            var s = new SettingsStore(file).Load();

            Assert.Equal(["yt-dlp", "yt-dlp-nightly", "yt-dlp-master", "youtube-dl"], s.Engines.Take(4).Select(e => e.Id));
            Assert.Equal("yt-dlp/yt-dlp", s.FindEngine("yt-dlp")!.GitHubRepo); // built-in definitions come from code
            var custom = Assert.Single(s.Engines, e => !e.IsManaged);
            Assert.Equal(@"C:\tools\yt-dlp-fork.exe", custom.ExecutablePath);
            Assert.Equal("fork", custom.Id);
            Assert.Null(s.Presets[0].EngineId); // pointed at a downloader that no longer exists
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Preset_engine_falls_back_to_default()
    {
        var s = SettingsStore.CreateDefaults(); // engines are only filled in by Load(), so add them here
        foreach (var e in Engine.CreateBuiltIns())
        {
            s.Engines.Add(e);
        }

        var preset = s.Presets[0];
        Assert.Equal(Engine.StableId, s.EngineFor(preset).Id);
        preset.EngineId = "youtube-dl";
        Assert.Equal(EngineFlavor.YoutubeDl, s.EngineFor(preset).Flavor);
    }

    [Fact]
    public void Removing_things_drops_the_choices_that_pointed_at_them()
    {
        var s = SettingsStore.CreateDefaults();
        var custom = new Engine { Name = "Fork", ExecutablePath = "fork" };
        var cookies = new CookieSource { Name = "Work" };
        s.Engines = [.. Engine.CreateBuiltIns(), custom];
        s.CookieSources.Add(cookies);
        s.DefaultEngineId = custom.Id;
        s.Presets[1].EngineId = custom.Id;
        s.LastCookieId = cookies.Id;
        var deletedPreset = s.Presets[0]; // the default
        var rule = new RoutingRule { PresetId = deletedPreset.Id, CookieId = cookies.Id };
        var browserRule = new RoutingRule { PresetId = s.Presets[1].Id, CookieId = Cookies.BrowserId("firefox") };
        s.Rules = [rule, browserRule];

        s.Engines.Remove(custom);
        s.CookieSources.Remove(cookies);
        s.Presets.Remove(deletedPreset);
        s.RemoveDanglingReferences();

        Assert.Equal(Engine.StableId, s.DefaultEngineId);
        Assert.Null(s.Presets[0].EngineId);
        Assert.Equal(s.Presets[0].Id, s.DefaultPresetId);
        Assert.Null(s.LastCookieId);
        Assert.Equal((null, null), (rule.PresetId, rule.CookieId));
        Assert.Equal((s.Presets[0].Id, Cookies.BrowserId("firefox")), (browserRule.PresetId, browserRule.CookieId));
    }
}
