using System.IO.Compression;
using System.Runtime.InteropServices;

namespace VidArchiverGui.Core.Services;

/// <summary>ffmpeg: merges separate video and audio, and embeds thumbnails, subtitles and metadata.</summary>
public sealed partial class ToolManager
{
    private const string FfmpegReleaseBase = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/";

    private static readonly SemaphoreSlim FfmpegGate = new(1);

    private static string ManagedFfmpegPath => Path.Combine(AppPaths.BinDir, AppPaths.ExeName("ffmpeg"));

    /// <summary>Returns a value suitable for --ffmpeg-location (a folder or executable), or null to let the downloader search PATH.</summary>
    public string? ResolveFfmpeg()
    {
        if (!string.IsNullOrWhiteSpace(settings.FfmpegPath))
        {
            return File.Exists(settings.FfmpegPath) || Directory.Exists(settings.FfmpegPath) ? settings.FfmpegPath : null;
        }

        if (File.Exists(ManagedFfmpegPath))
        {
            return ManagedFfmpegPath;
        }

        return FindOnPath("ffmpeg");
    }

    public async Task<string?> GetFfmpegVersionAsync(CancellationToken ct = default)
    {
        var location = ResolveFfmpeg();
        if (location is null)
        {
            return null;
        }

        var exe = Directory.Exists(location) ? Path.Combine(location, AppPaths.ExeName("ffmpeg")) : location;
        return await TryRunAsync(exe, ["-version"], ct) is { ExitCode: 0 } r ? r.StdOut.Split('\n', 2)[0].Trim() : null;
    }

    /// <summary>
    /// Windows builds are .zip; Linux builds are .tar.xz, which .NET can't unpack itself, so we use the system's tar
    /// (present on practically every distro). macOS has no official static builds: Homebrew is the way there.
    /// </summary>
    public static bool CanDownloadFfmpeg => OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && FindOnPath("tar") is not null);

    public static string FfmpegInstallHint =>
        OperatingSystem.IsMacOS() ? "Install ffmpeg with Homebrew: brew install ffmpeg"
        : OperatingSystem.IsLinux() ? "Install ffmpeg with your package manager, e.g. " + (FfmpegInstallCommand() ?? "sudo apt install ffmpeg")
        : "Use the Download button to install ffmpeg.";

    /// <summary>The install command for this system's package manager, if one is recognised.</summary>
    public static string? FfmpegInstallCommand()
    {
        if (OperatingSystem.IsMacOS())
        {
            return FindOnPath("brew") is not null ? "brew install ffmpeg" : null;
        }

        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        if (FindOnPath("apt-get") is not null)
        {
            return "sudo apt install ffmpeg";
        }

        if (FindOnPath("dnf") is not null)
        {
            return "sudo dnf install ffmpeg";
        }

        if (FindOnPath("pacman") is not null)
        {
            return "sudo pacman -S ffmpeg";
        }

        if (FindOnPath("zypper") is not null)
        {
            return "sudo zypper install ffmpeg";
        }

        return null;
    }

    public static Task DownloadFfmpegAsync(IProgress<TransferProgress>? progress = null, CancellationToken ct = default) =>
        CanDownloadFfmpeg
            ? WithGate(FfmpegGate, () => DownloadFfmpegCoreAsync(progress, ct), ct)
            : throw new PlatformNotSupportedException(FfmpegInstallHint);

    private static async Task DownloadFfmpegCoreAsync(IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var asset = FfmpegAsset();
        var archivePath = Path.Combine(Path.GetTempPath(), "vidarchivergui-" + asset);
        var extractDir = Path.Combine(Path.GetTempPath(), "vidarchivergui-ffmpeg-" + Guid.NewGuid().ToString("N"));
        try
        {
            // FFmpeg-Builds re-publishes "latest" in place; if it changes between these two requests the hash
            // check fails and the user can simply retry.
            var expected = ReleaseDownloader.FindChecksum(await ReleaseDownloader.GetTextAsync(FfmpegReleaseBase + "checksums.sha256", ct), asset)
                ?? throw new InvalidDataException($"{asset} is not listed in the release checksums; refusing to install it.");
            await ReleaseDownloader.DownloadFileAsync(FfmpegReleaseBase + asset, archivePath, progress, ct, expected);

            if (asset.EndsWith(".zip", StringComparison.Ordinal))
            {
                ZipFile.ExtractToDirectory(archivePath, extractDir);
            }
            else
            {
                Directory.CreateDirectory(extractDir);
                var tar = await ProcessHelper.RunAsync(FindOnPath("tar")!, ["-xJf", archivePath, "-C", extractDir], ct);
                if (tar.ExitCode != 0)
                {
                    throw new InvalidDataException("Could not unpack ffmpeg (tar): " + tar.StdErr.Trim());
                }
            }

            foreach (var name in new[] { AppPaths.ExeName("ffmpeg"), AppPaths.ExeName("ffprobe") })
            {
                var source = Directory.EnumerateFiles(extractDir, name, SearchOption.AllDirectories)
                    .FirstOrDefault(f => Path.GetFileName(Path.GetDirectoryName(f)) == "bin")
                    ?? throw new InvalidDataException($"{name} not found in {asset}");
                var target = Path.Combine(AppPaths.BinDir, name);
                File.Copy(source, target + ".download", overwrite: true);
                ReleaseDownloader.ReplaceFile(target + ".download", target);
                ReleaseDownloader.MakeExecutable(target);
            }
        }
        finally
        {
            File.Delete(archivePath);
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, recursive: true);
            }
        }
    }

    private static string FfmpegAsset()
    {
        var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsWindows())
        {
            return arm ? "ffmpeg-master-latest-winarm64-gpl.zip" : "ffmpeg-master-latest-win64-gpl.zip";
        }

        return arm ? "ffmpeg-master-latest-linuxarm64-gpl.tar.xz" : "ffmpeg-master-latest-linux64-gpl.tar.xz";
    }
}
