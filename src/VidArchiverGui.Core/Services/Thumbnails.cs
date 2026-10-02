namespace VidArchiverGui.Core.Services;

/// <summary>A downloaded file's thumbnail, taken from files on this computer only (never the network).</summary>
public static class Thumbnails
{
    private static readonly string[] ImageExtensions = [".webp", ".jpg", ".jpeg", ".png"];

    /// <summary>The thumbnail saved next to <paramref name="mediaFile"/> by --write-thumbnail: the same name with an image extension.</summary>
    public static string? FindSidecar(string mediaFile)
    {
        var stem = Path.Combine(Path.GetDirectoryName(mediaFile) ?? "", Path.GetFileNameWithoutExtension(mediaFile));
        return ImageExtensions.Select(e => stem + e).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Saves the cover embedded in <paramref name="mediaFile"/> by --embed-thumbnail as a small PNG. Returns false if
    /// it has none (or ffmpeg couldn't read it).
    /// </summary>
    /// <param name="ffmpegLocation">See <see cref="ToolManager.ResolveFfmpeg"/>.</param>
    public static async Task<bool> ExtractEmbeddedAsync(string mediaFile, string ffmpegLocation, string pngFile, CancellationToken ct = default)
    {
        var exe = Directory.Exists(ffmpegLocation) ? Path.Combine(ffmpegLocation, AppPaths.ExeName("ffmpeg")) : ffmpegLocation;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        // All video streams minus the ones that aren't pictures leaves the covers, whether the file keeps them as an
        // attached picture (MP4, MP3) or an attachment (MKV).
        var result = await ProcessHelper.RunAsync(exe,
            ["-v", "error", "-y", "-i", mediaFile, "-map", "0:v", "-map", "-0:V", "-frames:v", "1", "-vf", "scale=320:-2", pngFile], timeout.Token);
        return result.ExitCode == 0 && File.Exists(pngFile);
    }
}
