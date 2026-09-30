using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VidArchiverGui.Core.Services;

/// <summary>
/// Fetches tools from GitHub releases: release info, checksum lists, and checksum-verified downloads that replace the
/// installed file only once they're complete.
/// </summary>
internal static partial class ReleaseDownloader
{
    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>
    /// A tool download gives up only when it stops receiving data for this long; a slow but moving download is never cut
    /// off (the user can cancel it instead).
    /// </summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(2);

    public static async Task<string?> GetLatestTagAsync(string repo, CancellationToken ct)
    {
        var json = await GetTextAsync($"https://api.github.com/repos/{repo}/releases/latest", ct,
            req => req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json")));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("tag_name").GetString();
    }

    /// <summary>A small request (release info, checksum list), limited to <see cref="ToolManager.QueryTimeout"/>.</summary>
    public static async Task<string> GetTextAsync(string url, CancellationToken ct, Action<HttpRequestMessage>? configure = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ToolManager.QueryTimeout);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        configure?.Invoke(req);
        try
        {
            using var resp = await Http.SendAsync(req, timeout.Token);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{new Uri(url).Host} didn't answer within {ToolManager.QueryTimeout.TotalSeconds:0} s.");
        }
    }

    /// <summary>Finds the SHA-256 for <paramref name="fileName"/> in a "hash  name" (or "hash *name") checksum list.</summary>
    public static string? FindChecksum(string checksumList, string fileName)
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

    /// <summary>deno's .sha256sum files use PowerShell's "Hash : ..." layout on Windows and "hash  name" elsewhere.</summary>
    public static string? FindAnySha256(string text) =>
        Sha256Regex().Match(text) is { Success: true } m ? m.Value.ToLowerInvariant() : null;

    /// <summary>Downloads to a temp file, verifies its SHA-256, and only then replaces <paramref name="destination"/>.</summary>
    public static async Task DownloadFileAsync(string url, string destination, IProgress<TransferProgress>? progress, CancellationToken ct,
        string expectedSha256, TimeSpan? stallTimeout = null)
    {
        var stallAfter = stallTimeout ?? StallTimeout;
        var name = Path.GetFileName(new Uri(url).LocalPath);
        progress?.Report(new TransferProgress(name, 0, null, 0) { Connecting = true });

        // Restarted every time data arrives, so only a download that has stopped moving times out.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(stallAfter);
        var tmp = destination + ".download";
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var src = await resp.Content.ReadAsStreamAsync(stall.Token))
            await using (var dst = File.Create(tmp))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                var clock = Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;
                progress?.Report(new TransferProgress(name, 0, total, 0));
                while ((read = await src.ReadAsync(buffer, stall.Token)) > 0)
                {
                    stall.CancelAfter(stallAfter);
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.AppendData(buffer, 0, read);
                    done += read;
                    if (clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(250))
                    {
                        lastReport = clock.Elapsed;
                        progress?.Report(new TransferProgress(name, done, total, done / clock.Elapsed.TotalSeconds));
                    }
                }

                progress?.Report(new TransferProgress(name, done, total, done / Math.Max(clock.Elapsed.TotalSeconds, 0.001)));
            }

            var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
            if (actual != expectedSha256)
            {
                throw new InvalidDataException($"Checksum mismatch for {name} (expected {expectedSha256}, got {actual}); the download was discarded.");
            }

            ReplaceFile(tmp, destination);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var quiet = stallAfter.TotalMinutes >= 1 ? $"{stallAfter.TotalMinutes:0} minutes" : $"{stallAfter.TotalSeconds:0} seconds";
            throw new TimeoutException($"Downloading {name} stopped: no data arrived for {quiet}.");
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
    public static void ReplaceFile(string source, string destination)
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

    public static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        // No overall time limit: small requests use QueryTimeout and downloads use StallTimeout instead, so a slow
        // connection can still finish a large download.
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var version = typeof(ReleaseDownloader).Assembly.GetName().Version?.ToString(3) ?? "0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VidArchiverGui/" + version);
        return client;
    }

    [GeneratedRegex(@"\b[0-9a-fA-F]{64}\b")]
    private static partial Regex Sha256Regex();
}
