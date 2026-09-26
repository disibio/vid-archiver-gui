using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

/// <summary>A downloader that has been located on disk, plus any arguments it needs to find the app's helper tools.</summary>
public sealed record ResolvedEngine(Engine Engine, string Path)
{
    public EngineFlavor Flavor => Engine.Flavor;

    /// <summary>Extra arguments for every invocation (e.g. pointing yt-dlp at the app-installed deno).</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];
}

/// <summary>Locates, installs and updates the downloaders (yt-dlp, its channels, youtube-dl, custom forks) and ffmpeg.</summary>
public sealed class ToolManager(AppSettings settings)
{
    private static readonly HttpClient Http = CreateHttpClient();

    private const string FfmpegReleaseBase = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/";

    private static string ManagedFfmpegPath => Path.Combine(AppPaths.BinDir, AppPaths.ExeName("ffmpeg"));

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

    // Installs and rollbacks write the same files; downloads that fall back to another downloader may install one
    // at the same time as the Settings tab does.
    private static readonly SemaphoreSlim InstallGate = new(1);

    public ResolvedEngine Resolve(Engine engine) =>
        LocatePath(engine) is { } path
            ? new ResolvedEngine(engine, path) { ExtraArgs = JsRuntimeArgs(engine.Flavor) }
            : throw new YtDlpException(engine.IsManaged
                ? $"{engine.Name} is not installed. Install it on the Settings tab."
                : $"{engine.Name}: executable not found at {engine.ExecutablePath}");

    /// <summary>
    /// yt-dlp finds deno on PATH by itself. Only the app-installed copy (used when deno isn't on PATH) needs pointing
    /// out; doing it only then keeps older forks without --js-runtimes working.
    /// </summary>
    private static IReadOnlyList<string> JsRuntimeArgs(EngineFlavor flavor) =>
        flavor == EngineFlavor.YtDlp && FindOnPath("deno") is null && File.Exists(ManagedDenoPath)
            ? ["--js-runtimes", "deno:" + ManagedDenoPath]
            : [];

    public ResolvedEngine ResolveFor(Preset preset) => Resolve(settings.EngineFor(preset));

    public async Task InstallAsync(Engine engine, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!engine.IsManaged)
        {
            throw new InvalidOperationException($"{engine.Name} is a custom executable and can't be installed by the app.");
        }

        await WithInstallGate(() => InstallCoreAsync(engine, progress, ct), ct);
    }

    /// <summary>Installs <paramref name="engine"/> unless it's already there (e.g. installed meanwhile by another download).</summary>
    public Task EnsureInstalledAsync(Engine engine, CancellationToken ct = default) =>
        WithInstallGate(() => IsInstalledByApp(engine) ? Task.CompletedTask : InstallCoreAsync(engine, null, ct), ct);

    private static async Task WithInstallGate(Func<Task> action, CancellationToken ct)
    {
        await InstallGate.WaitAsync(ct);
        try
        {
            await action();
        }
        finally
        {
            InstallGate.Release();
        }
    }

    private async Task InstallCoreAsync(Engine engine, IProgress<double>? progress, CancellationToken ct)
    {
        var path = ManagedPath(engine);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Pin to one release tag so the checksum list and the binary are guaranteed to match.
        var tag = await GetLatestVersionAsync(engine, ct) ?? throw new InvalidDataException($"No release found for {engine.GitHubRepo}");
        var releaseBase = $"https://github.com/{engine.GitHubRepo}/releases/download/{tag}/";
        var asset = AssetName(engine.Flavor);
        var expected = FindChecksum(await Http.GetStringAsync(releaseBase + "SHA2-256SUMS", ct), asset)
            ?? throw new InvalidDataException($"{asset} is not listed in the release checksums; refusing to install it.");

        // Keep the version being replaced (unless it's the same release, e.g. a re-download) so it can be rolled back to.
        if (File.Exists(path) && await GetVersionAsync(path, ct) != tag)
        {
            var previous = PreviousPath(engine);
            Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
            File.Copy(path, previous + ".download", overwrite: true);
            ReplaceFile(previous + ".download", previous);
        }

        await DownloadFileAsync(releaseBase + asset, path, progress, ct, expected);
        MakeExecutable(path);
    }

    /// <summary>Swaps the current and previous versions, so rolling back can itself be undone.</summary>
    public Task RollbackAsync(Engine engine, CancellationToken ct = default) => WithInstallGate(() =>
    {
        if (!CanRollback(engine))
        {
            throw new InvalidOperationException($"No previous version of {engine.Name} is kept.");
        }

        var current = ManagedPath(engine);
        var previous = PreviousPath(engine);
        File.Copy(current, previous + ".swap", overwrite: true);
        File.Copy(previous, current + ".download", overwrite: true);
        ReplaceFile(current + ".download", current);
        MakeExecutable(current);
        ReplaceFile(previous + ".swap", previous);
        return Task.CompletedTask;
    }, ct);

    /// <summary>Finds the SHA-256 for <paramref name="fileName"/> in a "hash  name" (or "hash *name") checksum list.</summary>
    internal static string? FindChecksum(string checksumList, string fileName)
    {
        foreach (var line in checksumList.Split('\n'))
        {
            var parts = line.Trim().Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Trim().TrimStart('*') == fileName && parts[0].Length == 64)
            {
                return parts[0].ToLowerInvariant();
            }
        }
        return null;
    }

    public async Task<string?> GetLatestVersionAsync(Engine engine, CancellationToken ct = default)
    {
        return engine.IsManaged ? await GetLatestTagAsync(engine.GitHubRepo!, ct) : null;
    }

    /// <summary>Runs "-U" for a downloader the app doesn't manage (works for official yt-dlp/youtube-dl binaries).</summary>
    public async Task<string> SelfUpdateAsync(string exe, CancellationToken ct = default)
    {
        var r = await ProcessHelper.RunAsync(exe, ["-U"], ct);
        return (r.StdOut + "\n" + r.StdErr).Trim();
    }

    public async Task<string?> GetVersionAsync(string exe, CancellationToken ct = default) =>
        await TryRunAsync(exe, ["--version"], ct) is { ExitCode: 0 } r ? r.StdOut.Trim() : null;

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

    // ---------- deno (JavaScript runtime) ----------

    /// <summary>
    /// yt-dlp needs deno to solve YouTube's JavaScript challenges. It supports node/bun/quickjs too, but only deno is
    /// enabled by default, so deno is what we look for.
    /// </summary>
    public static string? ResolveDeno() => FindOnPath("deno") ?? (File.Exists(ManagedDenoPath) ? ManagedDenoPath : null);

    public static string ManagedDenoPath => Path.Combine(AppPaths.BinDir, AppPaths.ExeName("deno"));

    public static bool IsDenoInstalledByApp => File.Exists(ManagedDenoPath);

    public static bool HasOtherJsRuntime => FindOnPath("node") is not null || FindOnPath("bun") is not null;

    public async Task<string?> GetLatestDenoVersionAsync(CancellationToken ct = default) =>
        (await GetLatestTagAsync("denoland/deno", ct))?.TrimStart('v');

    // "deno 2.9.7 (stable, release, x86_64-pc-windows-msvc)"
    public async Task<string?> GetDenoVersionAsync(string exe, CancellationToken ct = default) =>
        await TryRunAsync(exe, ["--version"], ct) is { ExitCode: 0 } r
        && r.StdOut.Split('\n', 2)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries) is [_, var version, ..]
            ? version
            : null;

    public async Task InstallDenoAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var tag = await GetLatestTagAsync("denoland/deno", ct) ?? throw new InvalidDataException("No deno release found.");
        var asset = $"deno-{DenoTarget()}.zip";
        var releaseBase = $"https://github.com/denoland/deno/releases/download/{tag}/";
        var expected = FindAnySha256(await Http.GetStringAsync(releaseBase + asset + ".sha256sum", ct))
            ?? throw new InvalidDataException($"No checksum published for {asset}; refusing to install it.");

        var zipPath = Path.Combine(Path.GetTempPath(), "vidarchivergui-" + asset);
        try
        {
            await DownloadFileAsync(releaseBase + asset, zipPath, progress, ct, expected);
            using var zip = ZipFile.OpenRead(zipPath);
            var exe = AppPaths.ExeName("deno");
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(exe, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"{exe} not found in {asset}");
            var tmp = ManagedDenoPath + ".download";
            entry.ExtractToFile(tmp, overwrite: true);
            ReplaceFile(tmp, ManagedDenoPath);
            MakeExecutable(ManagedDenoPath);
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

    /// <summary>deno's .sha256sum files use PowerShell's "Hash : ..." layout on Windows and "hash  name" elsewhere.</summary>
    internal static string? FindAnySha256(string text) =>
        System.Text.RegularExpressions.Regex.Match(text, @"\b[0-9a-fA-F]{64}\b") is { Success: true } m ? m.Value.ToLowerInvariant() : null;

    private static async Task<string?> GetLatestTagAsync(string repo, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("tag_name").GetString();
    }

    // ---------- ffmpeg ----------

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

    /// <summary>
    /// Windows builds are .zip; Linux builds are .tar.xz, which .NET can't unpack itself, so we use the system's tar
    /// (present on practically every distro). macOS has no official static builds: Homebrew is the way there.
    /// </summary>
    public bool CanDownloadFfmpeg => OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && FindOnPath("tar") is not null);

    public static string FfmpegInstallHint =>
        OperatingSystem.IsMacOS() ? "Install ffmpeg with Homebrew: brew install ffmpeg"
        : OperatingSystem.IsLinux() ? "Install ffmpeg with your package manager, e.g. sudo apt install ffmpeg"
        : "Use the Download button to install ffmpeg.";

    private static string FfmpegAsset()
    {
        var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsWindows())
        {
            return arm ? "ffmpeg-master-latest-winarm64-gpl.zip" : "ffmpeg-master-latest-win64-gpl.zip";
        }

        return arm ? "ffmpeg-master-latest-linuxarm64-gpl.tar.xz" : "ffmpeg-master-latest-linux64-gpl.tar.xz";
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

    public async Task DownloadFfmpegAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!CanDownloadFfmpeg)
        {
            throw new PlatformNotSupportedException(FfmpegInstallHint);
        }

        var asset = FfmpegAsset();
        var archivePath = Path.Combine(Path.GetTempPath(), "vidarchivergui-" + asset);
        var extractDir = Path.Combine(Path.GetTempPath(), "vidarchivergui-ffmpeg-" + Guid.NewGuid().ToString("N"));
        try
        {
            // FFmpeg-Builds re-publishes "latest" in place; if it changes between these two requests the hash
            // check fails and the user can simply retry.
            var expected = FindChecksum(await Http.GetStringAsync(FfmpegReleaseBase + "checksums.sha256", ct), asset)
                ?? throw new InvalidDataException($"{asset} is not listed in the release checksums; refusing to install it.");
            await DownloadFileAsync(FfmpegReleaseBase + asset, archivePath, progress, ct, expected);

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
                ReplaceFile(target + ".download", target);
                MakeExecutable(target);
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

    /// <summary>Runs a quick query such as --version; null if the program can't be started.</summary>
    private static async Task<ProcessResult?> TryRunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        try
        {
            return await ProcessHelper.RunAsync(exe, args, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Downloads to a temp file, verifies its SHA-256, and only then replaces <paramref name="destination"/>.</summary>
    private static async Task DownloadFileAsync(string url, string destination, IProgress<double>? progress, CancellationToken ct, string expectedSha256)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;

        var tmp = destination + ".download";
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        try
        {
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmp))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.AppendData(buffer, 0, read);
                    done += read;
                    if (total > 0)
                    {
                        progress?.Report((double)done / total.Value);
                    }
                }
            }

            var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
            if (actual != expectedSha256)
            {
                throw new InvalidDataException($"Checksum mismatch for {Path.GetFileName(url)} (expected {expectedSha256}, got {actual}); the download was discarded.");
            }

            ReplaceFile(tmp, destination);
        }
        finally
        {
            File.Delete(tmp); // no-op after a successful move
        }
    }

    /// <summary>
    /// Moves <paramref name="source"/> over <paramref name="destination"/>. Windows refuses to overwrite an executable
    /// that is running (e.g. yt-dlp during a download) but does allow renaming it, so the old copy is moved aside first;
    /// the running process keeps using it and the ".old" file is deleted on the next update.
    /// </summary>
    internal static void ReplaceFile(string source, string destination)
    {
        var old = destination + ".old";
        TryDelete(old);
        if (File.Exists(destination) && !File.Exists(old))
        {
            try
            {
                File.Move(destination, old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        File.Move(source, destination, overwrite: true);
        TryDelete(old);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var version = typeof(ToolManager).Assembly.GetName().Version?.ToString(3) ?? "0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VidArchiverGui/" + version);
        return client;
    }
}
