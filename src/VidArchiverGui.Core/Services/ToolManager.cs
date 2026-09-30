using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

/// <summary>A downloader that has been located on disk, plus any arguments it needs to find the app's helper tools.</summary>
public sealed record ResolvedEngine(Engine Engine, string Path)
{
    public EngineFlavor Flavor => Engine.Flavor;

    /// <summary>Extra arguments for every invocation (e.g. pointing yt-dlp at the app-installed deno).</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];
}

/// <summary>
/// Locates, installs and updates the downloaders (yt-dlp, its channels, youtube-dl, custom forks), deno
/// (ToolManager.Deno.cs) and ffmpeg (ToolManager.Ffmpeg.cs). Members are static unless they depend on the settings.
/// </summary>
public sealed partial class ToolManager(AppSettings settings)
{
    /// <summary>How long a quick query like "--version" may take before it's treated as not answering.</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);

    // Installs and rollbacks of one tool write the same files, and can be started from several places at once: the
    // Settings tab, the setup checklist, and downloads that fall back to another downloader.
    private static readonly SemaphoreSlim InstallGate = new(1);

    private static readonly ConcurrentDictionary<(string Path, long Size, DateTime Modified), Task<string?>> VersionCache = new();

    // ---------- downloaders ----------

    /// <summary>Where the app keeps its own copy of a managed downloader.</summary>
    public static string ManagedPath(Engine engine) =>
        Path.Combine(AppPaths.BinDir, engine.Id, AppPaths.ExeName(engine.Flavor == EngineFlavor.YoutubeDl ? "youtube-dl" : "yt-dlp"));

    public static string? LocatePath(Engine engine)
    {
        if (!engine.IsManaged)
        {
            return File.Exists(engine.ExecutablePath) ? engine.ExecutablePath : null;
        }

        var managed = ManagedPath(engine);
        if (File.Exists(managed))
        {
            return managed;
        }
        // Only stable yt-dlp falls back to a copy on PATH.
        return engine.Id == Engine.StableId ? FindOnPath("yt-dlp") : null;
    }

    public static bool IsInstalledByApp(Engine engine) => engine.IsManaged && File.Exists(ManagedPath(engine));

    /// <summary>The version that was installed before the last update, kept so a bad release can be rolled back.</summary>
    public static string PreviousPath(Engine engine) =>
        Path.Combine(AppPaths.BinDir, "previous", engine.Id, Path.GetFileName(ManagedPath(engine)));

    public static bool CanRollback(Engine engine) => engine.IsManaged && File.Exists(PreviousPath(engine)) && File.Exists(ManagedPath(engine));

    public static ResolvedEngine Resolve(Engine engine) =>
        LocatePath(engine) is { } path
            ? new ResolvedEngine(engine, path) { ExtraArgs = JsRuntimeArgs(engine.Flavor) }
            : throw new YtDlpException(engine.IsManaged
                ? $"{engine.Name} is not installed. Install it on the Settings tab."
                : $"{engine.Name}: executable not found at {engine.ExecutablePath}");

    public ResolvedEngine ResolveFor(Preset preset) => Resolve(settings.EngineFor(preset));

    public static async Task InstallAsync(Engine engine, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        if (!engine.IsManaged)
        {
            throw new InvalidOperationException($"{engine.Name} is a custom executable and can't be installed by the app.");
        }

        await WithGate(InstallGate, () => InstallCoreAsync(engine, progress, ct), ct);
    }

    /// <summary>Installs <paramref name="engine"/> unless it's already there (e.g. installed meanwhile by another download).</summary>
    public static Task EnsureInstalledAsync(Engine engine, IProgress<TransferProgress>? progress = null, CancellationToken ct = default) =>
        WithGate(InstallGate, () => IsInstalledByApp(engine) ? Task.CompletedTask : InstallCoreAsync(engine, progress, ct), ct);

    private static async Task InstallCoreAsync(Engine engine, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var path = ManagedPath(engine);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Pin to one release tag so the checksum list and the binary are guaranteed to match.
        var tag = await GetLatestVersionAsync(engine, ct) ?? throw new InvalidDataException($"No release found for {engine.GitHubRepo}");
        var releaseBase = $"https://github.com/{engine.GitHubRepo}/releases/download/{tag}/";
        var asset = AssetName(engine.Flavor);
        var expected = ReleaseDownloader.FindChecksum(await ReleaseDownloader.GetTextAsync(releaseBase + "SHA2-256SUMS", ct), asset)
            ?? throw new InvalidDataException($"{asset} is not listed in the release checksums; refusing to install it.");

        // Keep the version being replaced (unless it's the same release, e.g. a re-download) so it can be rolled back to.
        if (File.Exists(path) && await GetVersionAsync(path, ct) != tag)
        {
            var previous = PreviousPath(engine);
            Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
            File.Copy(path, previous + ".download", overwrite: true);
            ReleaseDownloader.ReplaceFile(previous + ".download", previous);
        }

        await ReleaseDownloader.DownloadFileAsync(releaseBase + asset, path, progress, ct, expected);
        ReleaseDownloader.MakeExecutable(path);
    }

    /// <summary>Swaps the current and previous versions, so rolling back can itself be undone.</summary>
    public static Task RollbackAsync(Engine engine, CancellationToken ct = default) => WithGate(InstallGate, () =>
    {
        if (!CanRollback(engine))
        {
            throw new InvalidOperationException($"No previous version of {engine.Name} is kept.");
        }

        var current = ManagedPath(engine);
        var previous = PreviousPath(engine);
        File.Copy(current, previous + ".swap", overwrite: true);
        File.Copy(previous, current + ".download", overwrite: true);
        ReleaseDownloader.ReplaceFile(current + ".download", current);
        ReleaseDownloader.MakeExecutable(current);
        ReleaseDownloader.ReplaceFile(previous + ".swap", previous);
        return Task.CompletedTask;
    }, ct);

    public static Task<string?> GetLatestVersionAsync(Engine engine, CancellationToken ct = default) =>
        engine.IsManaged ? ReleaseDownloader.GetLatestTagAsync(engine.GitHubRepo!, ct) : Task.FromResult<string?>(null);

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

    private static string AssetName(EngineFlavor flavor)
    {
        if (flavor == EngineFlavor.YoutubeDl)
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

    public static string? FindOnPath(string name)
    {
        foreach (var dir in ProcessHelper.SearchPath())
        {
            var candidate = Path.Combine(dir, AppPaths.ExeName(name));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

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
