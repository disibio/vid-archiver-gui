using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

/// <summary>A downloader that has been located on disk, plus any arguments it needs to find the app's helper tools.</summary>
public sealed record ResolvedDownloader(Downloader Downloader, string Path)
{
    public DownloaderFlavor Flavor => Downloader.Flavor;

    /// <summary>Extra arguments for every invocation (e.g. pointing yt-dlp at the app-installed deno).</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];
}

/// <summary>
/// Locates, installs and updates the downloaders (yt-dlp, its channels, youtube-dl, custom forks), deno
/// (ToolManager.Deno.cs) and ffmpeg (ToolManager.Ffmpeg.cs).
/// </summary>
public static partial class ToolManager
{
    /// <summary>How long a quick query like "--version" may take before it's treated as not answering.</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);

    // Installs and rollbacks write into the app's bin folder and can be started from several places at once: the
    // Settings tab, the setup checklist, and downloads that fall back to another downloader. One at a time, for all
    // downloaders (deno and ffmpeg have their own gates).
    private static readonly SemaphoreSlim InstallGate = new(1);

    private static readonly ConcurrentDictionary<(string Path, long Size, DateTime Modified), Task<string?>> VersionCache = new();

    // ---------- downloaders ----------

    /// <summary>Where the app keeps its own copy of a managed downloader.</summary>
    public static string ManagedPath(Downloader downloader) =>
        Path.Combine(AppPaths.BinDir, downloader.Id, AppPaths.ExeName(downloader.Flavor == DownloaderFlavor.YoutubeDl ? "youtube-dl" : "yt-dlp"));

    public static string? LocatePath(Downloader downloader)
    {
        if (!downloader.IsManaged)
        {
            return File.Exists(downloader.ExecutablePath) ? downloader.ExecutablePath : null;
        }

        var managed = ManagedPath(downloader);
        if (File.Exists(managed))
        {
            return managed;
        }
        // Only stable yt-dlp falls back to a copy on PATH.
        return downloader.Id == Downloader.StableId ? ProcessHelper.FindOnPath("yt-dlp") : null;
    }

    public static bool IsInstalledByApp(Downloader downloader) => downloader.IsManaged && File.Exists(ManagedPath(downloader));

    /// <summary>The version that was installed before the last update, kept so a bad release can be rolled back.</summary>
    public static string PreviousPath(Downloader downloader) =>
        Path.Combine(AppPaths.BinDir, "previous", downloader.Id, Path.GetFileName(ManagedPath(downloader)));

    public static bool CanRollback(Downloader downloader) => downloader.IsManaged && File.Exists(PreviousPath(downloader)) && File.Exists(ManagedPath(downloader));

    public static ResolvedDownloader Resolve(Downloader downloader) =>
        LocatePath(downloader) is { } path
            ? new ResolvedDownloader(downloader, path) { ExtraArgs = JsRuntimeArgs(downloader.Flavor) }
            : throw new DownloaderException(downloader.IsManaged
                ? $"{downloader.Name} is not installed. Install it on the Settings tab."
                : $"{downloader.Name}: executable not found at {downloader.ExecutablePath}");

    public static async Task InstallAsync(Downloader downloader, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        if (!downloader.IsManaged)
        {
            throw new InvalidOperationException($"{downloader.Name} is a custom executable and can't be installed by the app.");
        }

        await WithGate(InstallGate, () => InstallCoreAsync(downloader, progress, ct), ct);
    }

    /// <summary>Installs <paramref name="downloader"/> unless it's already there (e.g. installed meanwhile by another download).</summary>
    public static Task EnsureInstalledAsync(Downloader downloader, IProgress<TransferProgress>? progress = null, CancellationToken ct = default) =>
        WithGate(InstallGate, () => IsInstalledByApp(downloader) ? Task.CompletedTask : InstallCoreAsync(downloader, progress, ct), ct);

    private static async Task InstallCoreAsync(Downloader downloader, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var path = ManagedPath(downloader);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Pin to one release tag so the checksum list and the binary are guaranteed to match.
        var tag = await GetLatestVersionAsync(downloader, ct) ?? throw new InvalidDataException($"No release found for {downloader.GitHubRepo}");
        var releaseBase = $"https://github.com/{downloader.GitHubRepo}/releases/download/{tag}/";
        var asset = AssetName(downloader.Flavor);
        var expected = ReleaseDownloader.FindChecksum(await ReleaseDownloader.GetTextAsync(releaseBase + "SHA2-256SUMS", ct), asset)
            ?? throw new InvalidDataException($"{asset} is not listed in the release checksums; refusing to install it.");

        // Keep the version being replaced (unless it's the same release, e.g. a re-download) so it can be rolled back to.
        if (File.Exists(path) && await GetVersionAsync(path, ct) != tag)
        {
            var previous = PreviousPath(downloader);
            Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
            File.Copy(path, previous + ".download", overwrite: true);
            ReleaseDownloader.ReplaceFile(previous + ".download", previous);
        }

        await ReleaseDownloader.DownloadFileAsync(releaseBase + asset, path, progress, ct, expected);
        ReleaseDownloader.MakeExecutable(path);
    }

    /// <summary>Swaps the current and previous versions, so rolling back can itself be undone.</summary>
    public static Task RollbackAsync(Downloader downloader, CancellationToken ct = default) => WithGate(InstallGate, () =>
    {
        if (!CanRollback(downloader))
        {
            throw new InvalidOperationException($"No previous version of {downloader.Name} is kept.");
        }

        var current = ManagedPath(downloader);
        var previous = PreviousPath(downloader);
        File.Copy(current, previous + ".swap", overwrite: true);
        File.Copy(previous, current + ".download", overwrite: true);
        ReleaseDownloader.ReplaceFile(current + ".download", current);
        ReleaseDownloader.MakeExecutable(current);
        ReleaseDownloader.ReplaceFile(previous + ".swap", previous);
        return Task.CompletedTask;
    }, ct);

    public static Task<string?> GetLatestVersionAsync(Downloader downloader, CancellationToken ct = default) =>
        downloader.IsManaged ? ReleaseDownloader.GetLatestTagAsync(downloader.GitHubRepo!, ct) : Task.FromResult<string?>(null);

    /// <summary>Runs "-U" for a downloader the app doesn't manage (works for official yt-dlp/youtube-dl binaries).</summary>
    public static async Task<string> SelfUpdateAsync(string exe, CancellationToken ct = default)
    {
        var r = await ProcessHelper.RunAsync(exe, ["-U"], ct);
        return (r.StdOut + "\n" + r.StdErr).Trim();
    }

    /// <summary>
    /// "exe --version", remembered until the file changes. Some builds are slow to start (the macOS yt-dlp unpacks
    /// itself into a temp folder that Gatekeeper then scans, ≈12 s per launch), so asking again for every refresh adds up.
    /// </summary>
    public static async Task<string?> GetVersionAsync(string exe, CancellationToken ct = default)
    {
        var file = new FileInfo(exe);
        if (!file.Exists)
        {
            return await RunVersionAsync(exe);
        }

        var key = (file.FullName, file.Length, file.LastWriteTimeUtc);
        var task = VersionCache.GetOrAdd(key, _ => RunVersionAsync(exe));
        var version = await task.WaitAsync(ct);
        if (version is null)
        {
            VersionCache.TryRemove(new(key, task)); // don't remember a failure or timeout
        }

        return version;
    }

    private static async Task<string?> RunVersionAsync(string exe) =>
        await TryRunAsync(exe, ["--version"], CancellationToken.None) is { ExitCode: 0 } r ? r.StdOut.Trim() : null;

    public static bool IsNewer(string? latest, string? current) =>
        Version.TryParse(latest, out var l) && (!Version.TryParse(current, out var c) || l > c);

    private static string AssetName(DownloaderFlavor flavor)
    {
        if (flavor == DownloaderFlavor.YoutubeDl)
        {
            return OperatingSystem.IsWindows() ? "youtube-dl.exe" : "youtube-dl"; // non-Windows build is a Python zipapp
        }

        if (OperatingSystem.IsWindows())
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X86 => "yt-dlp_x86.exe",
                Architecture.Arm64 => "yt-dlp_arm64.exe",
                _ => "yt-dlp.exe",
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return "yt-dlp_macos";
        }

        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "yt-dlp_linux_aarch64",
                Architecture.Arm => "yt-dlp_linux_armv7l",
                _ => "yt-dlp_linux",
            };
        }

        throw new PlatformNotSupportedException();
    }

    // ---------- helpers ----------

    /// <summary>Runs a quick query; null if it can't start, fails, or doesn't finish within <see cref="QueryTimeout"/>.</summary>
    internal static async Task<ProcessResult?> TryRunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QueryTimeout);
        try
        {
            return await ProcessHelper.RunAsync(exe, args, timeout.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task WithGate(SemaphoreSlim gate, Func<Task> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await action();
        }
        finally
        {
            gate.Release();
        }
    }
}
