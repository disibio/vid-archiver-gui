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
        Assert.Empty(Cookies.Args(s, null, EngineFlavor.YtDlp));
        Assert.Empty(Cookies.Args(s, Cookies.NoneId, EngineFlavor.YtDlp));
    }

    [Fact]
    public void Browser_choice_becomes_cookies_from_browser()
    {
        Assert.Equal(["--cookies-from-browser", "firefox"], Cookies.Args(new AppSettings(), Cookies.BrowserId("firefox"), EngineFlavor.YtDlp));
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

            Assert.Equal(["--cookies", file], Cookies.Args(s, txt.Id, EngineFlavor.YtDlp));
            Assert.Equal(["--cookies", file], Cookies.Args(s, txt.Id, EngineFlavor.YoutubeDl)); // youtube-dl can use files
            Assert.Equal(["--cookies-from-browser", "firefox:work"], Cookies.Args(s, profile.Id, EngineFlavor.YtDlp));
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

        Assert.Contains("not found", Assert.Throws<YtDlpException>(() => Cookies.Args(s, "gone-file", EngineFlavor.YtDlp)).Message);
        Assert.Contains("removed", Assert.Throws<YtDlpException>(() => Cookies.Args(s, "never-existed", EngineFlavor.YtDlp)).Message);
        Assert.Contains("youtube-dl", Assert.Throws<YtDlpException>(() => Cookies.Args(s, Cookies.BrowserId("chrome"), EngineFlavor.YoutubeDl)).Message);
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

        var ytdl = DownloadRunner.BuildArguments(new DownloadRequest("u", [], Dest, EngineFlavor.YoutubeDl) { Gentle = true }, null);
        Assert.DoesNotContain("--sleep-requests", ytdl);
        Assert.Contains("--sleep-interval", ytdl);
    }
}
