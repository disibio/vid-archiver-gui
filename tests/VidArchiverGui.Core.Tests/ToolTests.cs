using System.Diagnostics;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

/// <summary>Checking, installing and running the downloaders and other tools. Downloads over HTTP are in <see cref="ToolDownloadTests"/>.</summary>
public sealed class ToolTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vidarchivergui-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("2026.09.20", "2026.08.01", true)]
    [InlineData("2026.09.20", "2026.09.20", false)]
    [InlineData("2026.09.20", null, true)]
    [InlineData(null, "2026.09.20", false)]
    public void Version_comparison(string? latest, string? current, bool expected)
    {
        Assert.Equal(expected, ToolManager.IsNewer(latest, current));
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

    [Theory]
    [InlineData("\r\nAlgorithm : SHA256\r\nHash      : A0C3101B4158D1DFB7D6A78A7BF0F3DE80C96BB423C152A0C3101B4158D1DFB7\r\nPath      : C:\\x\\deno.zip\r\n")]
    [InlineData("a0c3101b4158d1dfb7d6a78a7bf0f3de80c96bb423c152a0c3101b4158d1dfb7  deno-x86_64-apple-darwin.zip\n")]
    public void Finds_sha256_in_either_deno_checksum_layout(string text)
    {
        Assert.Equal("a0c3101b4158d1dfb7d6a78a7bf0f3de80c96bb423c152a0c3101b4158d1dfb7", ReleaseDownloader.FindAnySha256(text));
    }

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
}
