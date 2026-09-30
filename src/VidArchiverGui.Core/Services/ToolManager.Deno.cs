using System.IO.Compression;
using System.Runtime.InteropServices;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

/// <summary>
/// deno, the JavaScript runtime yt-dlp needs to solve YouTube's JavaScript challenges. yt-dlp supports node/bun/quickjs
/// too, but only deno is enabled by default, so deno is what we look for.
/// </summary>
public static partial class ToolManager
{
    private const string DenoRepo = "denoland/deno";

    private static readonly SemaphoreSlim DenoGate = new(1);

    public static string ManagedDenoPath => Path.Combine(AppPaths.BinDir, AppPaths.ExeName("deno"));

    public static bool IsDenoInstalledByApp => File.Exists(ManagedDenoPath);

    public static string? ResolveDeno() => ProcessHelper.FindOnPath("deno") ?? (IsDenoInstalledByApp ? ManagedDenoPath : null);

    public static bool HasOtherJsRuntime => ProcessHelper.FindOnPath("node") is not null || ProcessHelper.FindOnPath("bun") is not null;

    /// <summary>
    /// yt-dlp finds deno on PATH by itself. Only the app-installed copy (used when deno isn't on PATH) needs pointing
    /// out; doing it only then keeps older forks without --js-runtimes working.
    /// </summary>
    private static IReadOnlyList<string> JsRuntimeArgs(DownloaderFlavor flavor) =>
        flavor == DownloaderFlavor.YtDlp && ProcessHelper.FindOnPath("deno") is null && IsDenoInstalledByApp
            ? ["--js-runtimes", "deno:" + ManagedDenoPath]
            : [];

    public static async Task<string?> GetLatestDenoVersionAsync(CancellationToken ct = default) =>
        (await ReleaseDownloader.GetLatestTagAsync(DenoRepo, ct))?.TrimStart('v');

    // "deno 2.9.7 (stable, release, x86_64-pc-windows-msvc)"
    public static async Task<string?> GetDenoVersionAsync(string exe, CancellationToken ct = default) =>
        await TryRunAsync(exe, ["--version"], ct) is { ExitCode: 0 } r
        && r.StdOut.Split('\n', 2)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries) is [_, var version, ..]
            ? version
            : null;

    public static Task InstallDenoAsync(IProgress<TransferProgress>? progress = null, CancellationToken ct = default) =>
        WithGate(DenoGate, () => InstallDenoCoreAsync(progress, ct), ct);

    private static async Task InstallDenoCoreAsync(IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var tag = await ReleaseDownloader.GetLatestTagAsync(DenoRepo, ct) ?? throw new InvalidDataException("No deno release found.");
        var asset = $"deno-{DenoTarget()}.zip";
        var releaseBase = $"https://github.com/{DenoRepo}/releases/download/{tag}/";
        var expected = ReleaseDownloader.FindAnySha256(await ReleaseDownloader.GetTextAsync(releaseBase + asset + ".sha256sum", ct))
            ?? throw new InvalidDataException($"No checksum published for {asset}; refusing to install it.");

        var zipPath = Path.Combine(Path.GetTempPath(), "vidarchivergui-" + asset);
        Directory.CreateDirectory(AppPaths.BinDir);
        try
        {
            await ReleaseDownloader.DownloadFileAsync(releaseBase + asset, zipPath, progress, ct, expected);
            using var zip = ZipFile.OpenRead(zipPath);
            var exe = AppPaths.ExeName("deno");
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(exe, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"{exe} not found in {asset}");
            var tmp = ManagedDenoPath + ".download";
            entry.ExtractToFile(tmp, overwrite: true);
            ReleaseDownloader.ReplaceFile(tmp, ManagedDenoPath);
            ReleaseDownloader.MakeExecutable(ManagedDenoPath);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    private static string DenoTarget()
    {
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";
        if (OperatingSystem.IsWindows())
        {
            return $"{arch}-pc-windows-msvc";
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"{arch}-apple-darwin";
        }

        if (OperatingSystem.IsLinux())
        {
            return $"{arch}-unknown-linux-gnu";
        }

        throw new PlatformNotSupportedException();
    }
}
