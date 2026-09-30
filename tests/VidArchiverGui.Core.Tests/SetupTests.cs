using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

public class SetupTests
{
    [Fact]
    public void Probe_reads_what_yt_dlp_can_use()
    {
        const string header = """
            [debug] Command-line config: ['-v']
            [debug] exe versions: ffmpeg 2023-10-16-git-5ddab49d48-full_build-www.gyan.dev (setts), ffprobe 2023-10-16
            [debug] Optional libraries: Cryptodome-3.23.0, brotli-1.2.0, yt_dlp_ejs-0.8.0
            [debug] JS runtimes: deno-2.9.5
            ERROR: You must provide at least one URL.
            """;
        var p = YtDlpProbe.Parse(header);
        Assert.True(p.HasFfmpeg);
        Assert.True(p.HasJsRuntime);
        Assert.Equal("deno-2.9.5", p.JsRuntimes);
        Assert.True(p.HasEjs);
    }

    [Fact]
    public void Probe_on_a_bare_machine()
    {
        var p = YtDlpProbe.Parse("[debug] exe versions: none\r\n[debug] Optional libraries: certifi-2026.07.22\r\n[debug] JS runtimes: none\r\n");
        Assert.False(p.HasFfmpeg);
        Assert.False(p.HasJsRuntime);
        Assert.False(p.HasEjs);
    }

    [Fact]
    public void Probe_without_header_knows_nothing()
    {
        var p = YtDlpProbe.Parse("youtube-dl: error: no such option: -v");
        Assert.Null(p.HasFfmpeg);
        Assert.Null(p.HasJsRuntime);
    }

    [Theory]
    [InlineData("\r\nAlgorithm : SHA256\r\nHash      : A0C3101B4158D1DFB7D6A78A7BF0F3DE80C96BB423C152A0C3101B4158D1DFB7\r\nPath      : C:\\x\\deno.zip\r\n")]
    [InlineData("a0c3101b4158d1dfb7d6a78a7bf0f3de80c96bb423c152a0c3101b4158d1dfb7  deno-x86_64-apple-darwin.zip\n")]
    public void Finds_sha256_in_either_deno_checksum_layout(string text)
    {
        Assert.Equal("a0c3101b4158d1dfb7d6a78a7bf0f3de80c96bb423c152a0c3101b4158d1dfb7", ReleaseDownloader.FindAnySha256(text));
    }

    [Theory]
    [InlineData("--download-archive \"E:\\ARCHIVE\\YT\\archive.txt\" --mtime", @"E:\ARCHIVE\YT\archive.txt")]
    [InlineData("--download-archive=a.txt --download-archive b.txt", "b.txt")]
    [InlineData("--mtime", null)]
    public void Reads_option_values(string preset, string? expected)
    {
        Assert.Equal(expected, ArgumentParser.GetOptionValue(ArgumentParser.Split(preset), "--download-archive"));
    }

    [Theory]
    [InlineData("--download-archive \"C:\\Old Place\\archive.txt\" --mtime", @"D:\New Place", "--download-archive \"D:\\New Place\\archive.txt\" --mtime")]
    [InlineData("--download-archive C:\\Old\\archive.txt --mtime", @"D:\New Place", "--download-archive \"D:\\New Place\\archive.txt\" --mtime")]
    [InlineData("--download-archive=C:\\Old\\a.txt", @"D:\New", "--download-archive=D:\\New\\a.txt")]
    public void Choosing_another_archive_folder_rewrites_the_preset(string arguments, string newFolder, string expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // drive-letter paths
        }

        var preset = new Preset { Arguments = arguments };
        var other = new Preset { Arguments = "--download-archive \"E:\\Elsewhere\\archive.txt\"" };
        var oldFolder = DownloadArchive.FolderOf(preset)!;

        var changed = DownloadArchive.MoveFolder([preset, other], oldFolder, newFolder);

        Assert.Equal([preset], changed);
        Assert.Equal(expected, preset.Arguments);
        Assert.Equal("--download-archive \"E:\\Elsewhere\\archive.txt\"", other.Arguments);
    }

    [Fact]
    public void Only_the_apps_own_archive_folder_is_created_automatically()
    {
        var app = AppPaths.AppVideosFolder;
        Assert.True(AppPaths.IsUnder(app, app));
        Assert.True(AppPaths.IsUnder(Path.Combine(app, "sub"), app + Path.DirectorySeparatorChar));
        Assert.False(AppPaths.IsUnder(app + " (old)", app));
        Assert.False(AppPaths.IsUnder(Path.GetTempPath(), app));

        var item = new SetupItem("Archive folder", SetupStatus.Warning, "", "") { Fix = SetupFix.MissingFolder, CanCreateFolder = true };
        Assert.False(item.CanAutoFix);
        Assert.True((item with { IsAppFolder = true }).CanAutoFix);
        Assert.False((item with { IsAppFolder = true, CanCreateFolder = false }).CanAutoFix);
    }

    [Fact]
    public void Extra_engine_args_are_passed_before_the_url()
    {
        var args = DownloadRunner.BuildArguments(new DownloadRequest("https://x", ["-f", "b"], "/d"), null, ["--js-runtimes", "deno:/app/deno"]);
        Assert.True(args.IndexOf("--js-runtimes") < args.IndexOf("--"));
        Assert.Equal("deno:/app/deno", args[args.IndexOf("--js-runtimes") + 1]);
    }
}
