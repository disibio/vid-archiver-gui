using System.Text;
using System.Text.RegularExpressions;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

/// <summary>
/// Expands destination folders such as <c>E:\ARCHIVE\{site}\{channel|uploader_id}</c>.
/// <list type="bullet">
/// <item><c>{name}</c> — any yt-dlp info field (channel, uploader_id, upload_date, ...) or a built-in: site, domain, playlist,
/// yyyy and mm (today), upload_yyyy and upload_mm (from upload_date).</item>
/// <item><c>{a|b|c}</c> or <c>{(a|b|c)}</c> — the first alternative that has a value.</item>
/// <item><c>{a|"text"}</c> — a quoted alternative is used literally.</item>
/// </list>
/// Values are made safe as a single folder name on every OS; if nothing resolves the segment becomes "Unknown".
/// </summary>
public static partial class PathTemplate
{
    public const string Help =
        "Tokens: {site} {domain} {playlist} {yyyy} {mm} (today's date) {upload_yyyy} {upload_mm} (upload date), or any yt-dlp field such as {channel} {channel_id} {uploader} {uploader_id} {upload_date} {title} {id}. " +
        "Fallbacks: {channel|uploader_id|\"Unknown channel\"} uses the first one that has a value.";

    public static string Expand(string template, MediaInfo info, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return "";
        }

        var date = now ?? DateTime.Now;
        // Expand ~ and %VARS% first so metadata values can never be interpreted as variables.
        return TokenRegex().Replace(ExpandHome(template), m =>
        {
            var alternatives = m.Groups[1].Value.Split('|', StringSplitOptions.TrimEntries);
            if (!alternatives.All(IsValidAlternative))
            {
                return m.Value; // not something we understand; leave it untouched
            }

            foreach (var alt in alternatives)
            {
                if (IsLiteral(alt))
                {
                    return SanitizeSegment(alt[1..^1]);
                }

                if (Resolve(alt, info, date) is { Length: > 0 } value)
                {
                    return SanitizeSegment(value);
                }
            }
            return "Unknown";
        });
    }

    private static string? Resolve(string name, MediaInfo info, DateTime date) => name.ToLowerInvariant() switch
    {
        "site" => info.Site,
        "domain" => info.Domain,
        "playlist" => info.Playlist,
        "yyyy" => date.ToString("yyyy"),
        "mm" => date.ToString("MM"),
        "upload_yyyy" => UploadDate(info)?[..4],
        "upload_mm" => UploadDate(info)?[4..6],
        _ => info.Fields.TryGetValue(name, out var v) ? v : null,
    };

    /// <summary>yt-dlp's upload_date (YYYYMMDD), if the info has a valid one. Playlists and channels usually don't.</summary>
    private static string? UploadDate(MediaInfo info) =>
        info.Fields.TryGetValue("upload_date", out var d) && d.Length == 8 && d.All(char.IsAsciiDigit) ? d : null;

    private static bool IsLiteral(string alt) => alt.Length >= 2 && alt[0] == '"' && alt[^1] == '"';

    private static bool IsValidAlternative(string alt) => IsLiteral(alt) || NameRegex().IsMatch(alt);

    public static string ExpandHome(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim());
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        }

        return path;
    }

    /// <summary>Turns an arbitrary string into something usable as one folder name on Windows, macOS and Linux.</summary>
    public static string SanitizeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c);
        }

        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length > 120)
        {
            result = result[..120].TrimEnd('.', ' ');
        }

        if (result.Length == 0 || result is "." or "..")
        {
            return "Unknown";
        }

        // Reserved device names on Windows.
        var stem = result.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
        {
            result = "_" + result;
        }

        return result;
    }

    // {name}, {a|b}, {(a|b)} — the optional parentheses are just decoration.
    [GeneratedRegex(@"\{\(?([^{}()]+?)\)?\}")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.]*$")]
    private static partial Regex NameRegex();
}
