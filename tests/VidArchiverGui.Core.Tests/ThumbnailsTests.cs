using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

public sealed class ThumbnailsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vidarchivergui-thumbnails-" + Guid.NewGuid().ToString("N"));

    public ThumbnailsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [1]);
        return path;
    }

    [Fact]
    public void The_sidecar_is_the_image_with_the_same_name()
    {
        var video = Touch("Talk. Part 1 [abc].mkv");
        Touch("Talk. Part 1 [abc].info.json");
        Touch("Other [def].webp");
        Assert.Null(Thumbnails.FindSidecar(video));

        var jpg = Touch("Talk. Part 1 [abc].jpg");
        Assert.Equal(jpg, Thumbnails.FindSidecar(video));
        var webp = Touch("Talk. Part 1 [abc].webp");
        Assert.Equal(webp, Thumbnails.FindSidecar(video)); // yt-dlp's usual format comes first
    }

    /// <summary>Needs ffmpeg (the app's copy or one on PATH); without it there's nothing to test, so it passes.</summary>
    [Fact]
    public async Task The_embedded_cover_is_extracted_when_there_is_one()
    {
        if (ToolManager.ResolveFfmpeg(null) is not { } ffmpeg)
        {
            return;
        }

        var exe = Directory.Exists(ffmpeg) ? Path.Combine(ffmpeg, AppPaths.ExeName("ffmpeg")) : ffmpeg;
        var cover = Path.Combine(_dir, "cover.png");
        var withCover = Path.Combine(_dir, "with cover.mka");
        var without = Path.Combine(_dir, "without.mka");
        await ProcessHelper.RunAsync(exe, ["-v", "error", "-f", "lavfi", "-i", "color=red:s=64x36:d=1", "-frames:v", "1", cover]);
        // How yt-dlp embeds a thumbnail in MKV: as an attachment.
        await ProcessHelper.RunAsync(exe, ["-v", "error", "-f", "lavfi", "-i", "sine=d=1", "-c:a", "flac",
            "-attach", cover, "-metadata:s:t", "mimetype=image/png", withCover]);
        await ProcessHelper.RunAsync(exe, ["-v", "error", "-f", "lavfi", "-i", "sine=d=1", "-c:a", "flac", without]);
        Assert.True(File.Exists(withCover) && File.Exists(without));

        var png = Path.Combine(_dir, "out.png");
        Assert.True(await Thumbnails.ExtractEmbeddedAsync(withCover, ffmpeg, png));
        Assert.True(new FileInfo(png).Length > 0);

        File.Delete(png);
        Assert.False(await Thumbnails.ExtractEmbeddedAsync(without, ffmpeg, png));
        Assert.False(File.Exists(png));
    }
}
