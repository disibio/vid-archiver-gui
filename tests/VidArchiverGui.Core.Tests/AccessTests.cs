using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

public class AccessTests
{
    private static readonly string Dest = Path.Combine(Path.GetTempPath(), "dest");

    [Fact]
    public void No_cookies_adds_nothing()
    {
        var s = new AppSettings();
        Assert.Empty(Cookies.Args(s, null, DownloaderFlavor.YtDlp));
        Assert.Empty(Cookies.Args(s, Cookies.NoneId, DownloaderFlavor.YtDlp));
    }

    [Fact]
    public void Browser_choice_becomes_cookies_from_browser()
    {
        Assert.Equal(["--cookies-from-browser", "firefox"], Cookies.Args(new AppSettings(), Cookies.BrowserId("firefox"), DownloaderFlavor.YtDlp));
    }

    [Fact]
    public void Custom_sources_use_their_file_or_spec()
    {
        var file = Path.GetTempFileName();
        try
        {
            var s = new AppSettings();
            var txt = new CookieSource { Name = "txt", Kind = CookieSourceKind.File, Value = $"\"{file}\"" };
            var profile = new CookieSource { Name = "work", Kind = CookieSourceKind.Browser, Value = "firefox:work" };
            s.CookieSources.Add(txt);
            s.CookieSources.Add(profile);

            Assert.Equal(["--cookies", file], Cookies.Args(s, txt.Id, DownloaderFlavor.YtDlp));
            Assert.Equal(["--cookies", file], Cookies.Args(s, txt.Id, DownloaderFlavor.YoutubeDl)); // youtube-dl can use files
            Assert.Equal(["--cookies-from-browser", "firefox:work"], Cookies.Args(s, profile.Id, DownloaderFlavor.YtDlp));
            Assert.Equal("work", Cookies.DisplayName(s, profile.Id));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Unusable_choices_explain_why()
    {
        var s = new AppSettings();
        s.CookieSources.Add(new CookieSource { Id = "gone-file", Kind = CookieSourceKind.File, Value = Path.Combine(Dest, "nope.txt") });

        Assert.Contains("not found", Assert.Throws<DownloaderException>(() => Cookies.Args(s, "gone-file", DownloaderFlavor.YtDlp)).Message);
        Assert.Contains("removed", Assert.Throws<DownloaderException>(() => Cookies.Args(s, "never-existed", DownloaderFlavor.YtDlp)).Message);
        Assert.Contains("youtube-dl", Assert.Throws<DownloaderException>(() => Cookies.Args(s, Cookies.BrowserId("chrome"), DownloaderFlavor.YoutubeDl)).Message);
    }

    [Theory]
    [InlineData("waterfox", "Waterfox")]
    [InlineData("librewolf", "LibreWolf")]
    [InlineData("floorp", "Floorp")]
    [InlineData("zen", "Zen")]
    public void Firefox_based_browsers_are_read_as_firefox_from_their_profile_folder(string key, string name)
    {
        var folders = Cookies.ProfileDirs(key).ToList();
        Assert.NotEmpty(folders); // known on every OS the tests run on

        var last = folders[^1];
        Assert.Equal("firefox:" + last, Cookies.BrowserSpec(key, dir => dir == last));
        Assert.StartsWith(name + " wasn't found", Assert.Throws<DownloaderException>(() => Cookies.BrowserSpec(key, _ => false)).Message);
        Assert.Equal("chrome", Cookies.BrowserSpec("chrome", _ => false)); // yt-dlp finds these itself
    }

    [Fact]
    public void Browsers_in_use_but_not_found_stay_in_the_choices()
    {
        var s = new AppSettings();
        var source = new CookieSource { Name = "work" };
        s.CookieSources.Add(source);
        Choice firefox = new(Cookies.BrowserId("firefox"), "Firefox");

        var choices = Cookies.Choices(s, [firefox],
            [Cookies.BrowserId("waterfox"), Cookies.BrowserId("waterfox"), firefox.Id, source.Id, Cookies.NoneId, null]);

        Assert.Equal(
            ["No cookies", "Firefox", "work", "Waterfox (not found on this computer)"],
            choices.Select(c => c.Name));
    }

    [Fact]
    public void App_cookie_choice_replaces_preset_cookie_options()
    {
        var request = new DownloadRequest("u", ["--cookies-from-browser", "chrome", "-f", "best", "--cookies=old.txt", "--no-cookies"], Dest)
        {
            CookieArgs = ["--cookies-from-browser", "firefox"],
        };
        var args = DownloadRunner.BuildArguments(request, null);

        Assert.Equal(["-f", "best", "--cookies-from-browser", "firefox", "-P", Dest], args.Take(6));
        Assert.DoesNotContain("chrome", args);
        Assert.DoesNotContain("--cookies=old.txt", args);
    }

    [Fact]
    public void Without_a_choice_preset_cookies_are_kept()
    {
        var args = DownloadRunner.BuildArguments(new DownloadRequest("u", ["--cookies-from-browser", "chrome"], Dest), null);
        Assert.Equal(["--cookies-from-browser", "chrome", "-P", Dest], args.Take(4));
        Assert.True(ArgumentParser.HasCookieOptions(["--cookies=x.txt"]));
        Assert.False(ArgumentParser.HasCookieOptions(["--no-cookies"]));
    }

    [Fact]
    public void Settings_load_drops_references_to_removed_sources()
    {
        var file = Path.Combine(Path.GetTempPath(), $"vidarchivergui-test-{Guid.NewGuid():N}.json");
        try
        {
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
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Gentle_options_come_before_the_preset_so_it_can_override_them()
    {
        var args = DownloadRunner.BuildArguments(new DownloadRequest("u", ["--sleep-interval", "30"], Dest) { Gentle = true }, null);
        Assert.Equal("--sleep-requests", args[0]);
        Assert.True(args.LastIndexOf("--sleep-interval") > args.IndexOf("--max-sleep-interval"));
        Assert.Equal("30", args[args.LastIndexOf("--sleep-interval") + 1]);

        var ytdl = DownloadRunner.BuildArguments(new DownloadRequest("u", [], Dest, DownloaderFlavor.YoutubeDl) { Gentle = true }, null);
        Assert.DoesNotContain("--sleep-requests", ytdl);
        Assert.Contains("--sleep-interval", ytdl);
    }
}
