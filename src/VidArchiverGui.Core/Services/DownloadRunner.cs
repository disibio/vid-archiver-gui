using System.Diagnostics;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

public sealed record DownloadRequest(string Url, IReadOnlyList<string> PresetArgs, string Destination, DownloaderFlavor Flavor = DownloaderFlavor.YtDlp)
{
    /// <summary>Cookie options chosen in the app; when present they replace any cookie options in the preset.</summary>
    public IReadOnlyList<string> CookieArgs { get; init; } = [];

    /// <summary>Adds <see cref="DownloadRunner.GentleArgs"/>.</summary>
    public bool Gentle { get; init; }
}

public static class DownloadRunner
{
    private const string YoutubeDlDefaultTemplate = "%(title)s-%(id)s.%(ext)s";

    /// <summary>
    /// Slower but far less likely to be throttled or hit "confirm you're not a bot": a pause before each request and
    /// between videos, and exponential back-off on retries. They go before the preset, so a preset's own values win.
    /// </summary>
    public static IReadOnlyList<string> GentleArgs(DownloaderFlavor flavor) => flavor == DownloaderFlavor.YtDlp
        ?
        [
            "--sleep-requests", "1", "--sleep-interval", "5", "--max-sleep-interval", "15",
            "--retry-sleep", "http:exp=1:60", "--retry-sleep", "fragment:exp=1:60", "--retry-sleep", "extractor:exp=1:60",
            "--extractor-retries", "5",
        ]
        : ["--sleep-interval", "5", "--max-sleep-interval", "15"]; // youtube-dl has no --sleep-requests/--retry-sleep

    public static List<string> BuildArguments(DownloadRequest request, string? ffmpegLocation, IReadOnlyList<string>? extraArgs = null)
    {
        List<string> args = request.Gentle ? [.. GentleArgs(request.Flavor)] : [];
        args.AddRange(ArgumentParser.WithCookies(request.PresetArgs, request.CookieArgs));
        if (request.Flavor == DownloaderFlavor.YtDlp)
        {
            // Our destination goes after the preset so it wins over any -P in the preset.
            args.AddRange(["-P", request.Destination]);
        }
        else
        {
            // youtube-dl has no -P: put the destination in front of the output template instead.
            var folder = request.Destination.Replace("%", "%%"); // a literal % in the path must not be read as a template field
            var i = FindOutputOption(args);
            if (i < 0)
            {
                args.AddRange(["-o", Path.Combine(folder, YoutubeDlDefaultTemplate)]);
            }
            else if (args[i].StartsWith("--output=", StringComparison.Ordinal))
            {
                args[i] = "--output=" + Prefix(folder, args[i]["--output=".Length..]);
            }
            else if (i + 1 < args.Count)
            {
                args[i + 1] = Prefix(folder, args[i + 1]);
            }
        }

        if (ffmpegLocation is not null)
        {
            args.AddRange(["--ffmpeg-location", ffmpegLocation]);
        }

        if (extraArgs is not null)
        {
            args.AddRange(extraArgs);
        }

        args.AddRange(["--newline", "--encoding", "utf-8"]);
        if (request.Flavor == DownloaderFlavor.YtDlp)
        {
            args.AddRange(["--progress-template", YtDlpOutputParser.ProgressTemplate]);
        }

        args.AddRange(["--", request.Url]);
        return args;

        static string Prefix(string folder, string template) => Path.IsPathRooted(template) ? template : Path.Combine(folder, template);
    }

    private static int FindOutputOption(List<string> args)
    {
        for (var i = args.Count - 1; i >= 0; i--)
        {
            if (args[i] is "-o" or "--output" || args[i].StartsWith("--output=", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Runs one download. Events are raised on a background thread. Returns the downloader's exit code.</summary>
    /// <param name="ffmpegLocation">See <see cref="ToolManager.ResolveFfmpeg"/>.</param>
    public static async Task<int> RunAsync(DownloadRequest request, ResolvedDownloader downloader, string? ffmpegLocation,
        Action<OutputEvent> onEvent, CancellationToken ct)
    {
        Directory.CreateDirectory(request.Destination);

        var args = BuildArguments(request with { Flavor = downloader.Flavor }, ffmpegLocation, downloader.ExtraArgs);
        onEvent(new OutputEvent.Text($"> {Path.GetFileName(downloader.Path)} " + string.Join(' ', args.Select(Quote))));

        using var process = new Process { StartInfo = ProcessHelper.CreateStartInfo(downloader.Path, args) };
        process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } l) { onEvent(YtDlpOutputParser.Parse(l)); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } l) { onEvent(YtDlpOutputParser.Parse(l)); } };

        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (ProcessHelper.KillOnCancel(process, ct))
        {
            await process.WaitForExitAsync(ct);
        }

        return process.ExitCode;
    }

    private static string Quote(string a) => a.Length == 0 || a.Any(char.IsWhiteSpace) || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;
}
