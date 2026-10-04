using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

/// <summary>Splitting a preset into arguments, and the full command line built for a download.</summary>
public class ArgumentsTests
{
    private static readonly string Dest = Path.Combine(Path.GetTempPath(), "dest");

    private static List<string> YoutubeDlArgs(params string[] preset) =>
        DownloadRunner.BuildArguments(new DownloadRequest("https://x", preset, Dest, DownloaderFlavor.YoutubeDl), null);

    [Fact]
    public void Splits_users_command_with_quoted_windows_path()
    {
        var args = ArgumentParser.Split(
            """-f bestvideo[height<=?1480]+bestaudio/best --write-comments  --download-archive "E:\ARCHIVE\YT\archive.txt" --merge-output-format mkv   --mtime""");

        Assert.Equal(
            ["-f", "bestvideo[height<=?1480]+bestaudio/best", "--write-comments", "--download-archive", @"E:\ARCHIVE\YT\archive.txt", "--merge-output-format", "mkv", "--mtime"],
            args);
    }

    [Fact]
    public void Handles_newlines_comments_and_leading_executable()
    {
        var args = ArgumentParser.Split("yt-dlp.exe -x\n# a comment --ignored\n  --audio-format 'mp3'\r\n");
        Assert.Equal(["-x", "--audio-format", "mp3"], args);
    }

    [Fact]
    public void Unterminated_quote_does_not_gain_a_trailing_newline()
    {
        Assert.Equal(["-o", "abc"], ArgumentParser.Split("-o \"abc"));
        Assert.Equal(["-o", "a\nb"], ArgumentParser.Split("-o \"a\nb"));
    }

    [Fact]
    public void Keeps_empty_quoted_argument()
    {
        Assert.Equal(["--sub-lang", ""], ArgumentParser.Split("--sub-lang \"\""));
    }

    [Fact]
    public void Extracts_only_access_options_for_metadata_lookup()
    {
        var args = ArgumentParser.Split("--write-comments --cookies-from-browser firefox -f best --proxy=socks5://x:1 --netrc --embed-subs");
        Assert.Equal(["--cookies-from-browser", "firefox", "--proxy=socks5://x:1", "--netrc"], ArgumentParser.ExtractAccessOptions(args));
    }

    [Theory]
    [InlineData("--download-archive \"E:\\ARCHIVE\\YT\\archive.txt\" --mtime", @"E:\ARCHIVE\YT\archive.txt")]
    [InlineData("--download-archive=a.txt --download-archive b.txt", "b.txt")]
    [InlineData("--mtime", null)]
    public void Reads_option_values(string preset, string? expected)
    {
        Assert.Equal(expected, ArgumentParser.GetOptionValue(ArgumentParser.Split(preset), "--download-archive"));
    }

    [Fact]
    public void Download_arguments_put_destination_after_preset_and_url_last()
    {
        var args = DownloadRunner.BuildArguments(new DownloadRequest("https://x", ["-P", "/old", "-f", "best"], "/new"), null);
        Assert.True(args.LastIndexOf("-P") > args.IndexOf("-f"));
        Assert.Equal("/new", args[args.LastIndexOf("-P") + 1]);
        Assert.Equal(["--", "https://x"], args[^2..]);
    }

    [Fact]
    public void Finished_files_are_listed_by_yt_dlp_only()
    {
        var request = new DownloadRequest("u", [], Dest) { FileListPath = "/tmp/100%/files.txt" };
        var args = DownloadRunner.BuildArguments(request, null);
        var i = args.IndexOf("--print-to-file");
        Assert.Equal(["after_move:filepath", "/tmp/100%%/files.txt"], args[(i + 1)..(i + 3)]);
        Assert.True(i < args.IndexOf("--"));

        Assert.DoesNotContain("--print-to-file", DownloadRunner.BuildArguments(request with { Flavor = DownloaderFlavor.YoutubeDl }, null));
        Assert.DoesNotContain("--print-to-file", DownloadRunner.BuildArguments(request with { FileListPath = null }, null));
    }

    [Fact]
    public void Ignore_errors_comes_before_the_preset_so_it_can_override_it()
    {
        var args = DownloadRunner.BuildArguments(new DownloadRequest("u", ["--abort-on-error"], Dest) { IgnoreErrors = true }, null);
        Assert.True(args.IndexOf("--ignore-errors") < args.IndexOf("--abort-on-error"));
        Assert.DoesNotContain("--ignore-errors", DownloadRunner.BuildArguments(new DownloadRequest("u", [], Dest), null));
    }

    [Fact]
    public void Ascii_names_restrict_file_names_too()
    {
        Assert.Contains("--restrict-filenames", DownloadRunner.BuildArguments(new DownloadRequest("u", [], Dest) { AsciiNames = true }, null));
        Assert.DoesNotContain("--restrict-filenames", DownloadRunner.BuildArguments(new DownloadRequest("u", [], Dest), null));
    }

    [Fact]
    public void Extra_downloader_args_are_passed_before_the_url()
    {
        var args = DownloadRunner.BuildArguments(new DownloadRequest("https://x", ["-f", "b"], "/d"), null, ["--js-runtimes", "deno:/app/deno"]);
        Assert.True(args.IndexOf("--js-runtimes") < args.IndexOf("--"));
        Assert.Equal("deno:/app/deno", args[args.IndexOf("--js-runtimes") + 1]);
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
        var args = DownloadRunner.BuildArguments(new DownloadRequest("u", [], Path.Combine(Dest, "100% legit"), DownloaderFlavor.YoutubeDl), null);
        Assert.StartsWith(Path.Combine(Dest, "100%% legit"), args[args.IndexOf("-o") + 1]);
    }
}
