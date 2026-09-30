using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

public class CookiesTests
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
}
