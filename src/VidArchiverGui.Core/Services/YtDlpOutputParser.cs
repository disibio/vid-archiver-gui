using System.Globalization;
using System.Text.RegularExpressions;

namespace VidArchiverGui.Core.Services;

public sealed record DownloadProgress(double? DownloadedBytes, double? TotalBytes, double? Speed, double? Eta)
{
    public double? Fraction => TotalBytes > 0 && DownloadedBytes is { } d ? Math.Clamp(d / TotalBytes.Value, 0, 1) : null;
}

public abstract record OutputEvent
{
    public sealed record Progress(DownloadProgress Value) : OutputEvent;
    public sealed record PlaylistItem(int Index, int Count) : OutputEvent;
    public sealed record Destination(string Path) : OutputEvent;
    public sealed record AlreadyDone(string Message) : OutputEvent;
    public sealed record Error(string Message) : OutputEvent;
    public sealed record PostProcessing(string Step) : OutputEvent;
    public sealed record Text(string Line) : OutputEvent;
}

public static partial class YtDlpOutputParser
{
    public const string ProgressPrefix = "[vidarchivergui-progress]";

    /// <summary>Passed to --progress-template so progress arrives as machine-readable numbers.</summary>
    public const string ProgressTemplate =
        "download:" + ProgressPrefix + " %(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s";

    public static OutputEvent Parse(string line)
    {
        if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
        {
            var parts = line[ProgressPrefix.Length..].Trim().Split('|');
            if (parts.Length >= 5)
            {
                return new OutputEvent.Progress(new DownloadProgress(Num(parts[0]), Num(parts[1]) ?? Num(parts[2]), Num(parts[3]), Num(parts[4])));
            }
        }

        if (line.StartsWith("ERROR:", StringComparison.Ordinal))
        {
            return new OutputEvent.Error(line[6..].Trim());
        }

        // Standard progress line (youtube-dl, or forks without --progress-template).
        if (StandardProgressRegex().Match(line) is { Success: true } sp)
        {
            var total = Bytes(sp.Groups["size"].Value, sp.Groups["su"].Value);
            var fraction = Num(sp.Groups["pct"].Value) / 100;
            return new OutputEvent.Progress(new DownloadProgress(
                total * fraction,
                total,
                sp.Groups["speed"].Success ? Bytes(sp.Groups["speed"].Value, sp.Groups["spu"].Value) : null,
                sp.Groups["eta"].Success ? ParseClock(sp.Groups["eta"].Value) : null));
        }

        if (PlaylistItemRegex().Match(line) is { Success: true } pm)
        {
            return new OutputEvent.PlaylistItem(int.Parse(pm.Groups[1].Value), int.Parse(pm.Groups[2].Value));
        }

        if (DestinationRegex().Match(line) is { Success: true } dm)
        {
            return new OutputEvent.Destination(dm.Groups[1].Value.Trim().Trim('"'));
        }

        if (line.Contains("has already been recorded in", StringComparison.Ordinal) // "in the archive" (yt-dlp) / "in archive" (youtube-dl)
            || line.Contains("has already been downloaded", StringComparison.Ordinal))
        {
            return new OutputEvent.AlreadyDone(line);
        }

        if (PostProcessorRegex().Match(line) is { Success: true } pp)
        {
            return new OutputEvent.PostProcessing(pp.Groups[1].Value);
        }

        return new OutputEvent.Text(line);
    }

    public static string? LastError(string output) =>
        output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal)) is { } e
            ? e[6..].Trim()
            : null;

    public static string FormatBytes(double? bytes)
    {
        if (bytes is not { } b || b < 0)
        {
            return "?";
        }

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var i = 0;
        while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
        return b.ToString(i == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[i];
    }

    public static string FormatEta(double? seconds)
    {
        if (seconds is not { } s || s < 0)
        {
            return "--:--";
        }

        var t = TimeSpan.FromSeconds(Math.Round(s));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    private static double? Num(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? Bytes(string number, string unit)
    {
        if (Num(number) is not { } n)
        {
            return null;
        }

        var binary = unit.Contains('i');
        var power = unit.Length > 1 ? "KMGTP".IndexOf(char.ToUpperInvariant(unit[0])) + 1 : 0;
        return n * Math.Pow(binary ? 1024 : 1000, power);
    }

    private static double? ParseClock(string s)
    {
        double total = 0;
        foreach (var part in s.Split(':'))
        {
            if (!int.TryParse(part, out var v))
            {
                return null;
            }

            total = total * 60 + v;
        }
        return total;
    }

    [GeneratedRegex(@"^\[download\]\s+(?<pct>\d+(?:\.\d+)?)%\s+of\s+~?\s*(?<size>\d+(?:\.\d+)?)\s*(?<su>[KMGTP]?i?B)(?:\s+at\s+(?<speed>\d+(?:\.\d+)?)\s*(?<spu>[KMGTP]?i?B)/s)?(?:\s+ETA\s+(?<eta>\d+(?::\d+)+))?")]
    private static partial Regex StandardProgressRegex();

    [GeneratedRegex(@"^\[download\] Downloading (?:item|video) (\d+) of (\d+)")]
    private static partial Regex PlaylistItemRegex();

    [GeneratedRegex(@"^\[(?:download|Merger|ExtractAudio|VideoConvertor|VideoRemuxer)\] (?:Destination: |Merging formats into )(.+)$")]
    private static partial Regex DestinationRegex();

    [GeneratedRegex(@"^\[(EmbedSubtitle|EmbedThumbnail|Metadata|FFmpegMetadata|ThumbnailsConvertor|SubtitlesConvertor|ModifyChapters|MoveFiles|FixupM3u8|FixupStretched|FixupM4a|FixupDuplicateMoov)\]")]
    private static partial Regex PostProcessorRegex();
}
