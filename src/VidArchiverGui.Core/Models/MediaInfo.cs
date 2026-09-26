using System.Text.Json;

namespace VidArchiverGui.Core.Models;

/// <summary>The subset of yt-dlp's info JSON that routing and the UI care about.</summary>
public sealed record MediaInfo
{
    public required string Url { get; init; }
    public string? Title { get; init; }
    public string? Site { get; init; }
    public string? Domain { get; init; }
    public string? Channel { get; init; }
    public string? ChannelId { get; init; }
    public string? Playlist { get; init; }
    public bool IsPlaylist { get; init; }
    public int? EntryCount { get; init; }

    /// <summary>
    /// Raw top-level scalar fields from yt-dlp's info JSON (channel, uploader_id, upload_date, ...), keyed by yt-dlp's
    /// own field names. For playlists, channel/uploader fields missing at the top are taken from the first entry.
    /// </summary>
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();

    private static readonly string[] OwnerFields = ["channel", "channel_id", "channel_url", "uploader", "uploader_id", "uploader_url"];

    public string? Get(MatchField field) => field switch
    {
        MatchField.Site => Site,
        MatchField.Domain => Domain,
        MatchField.Channel => Channel,
        MatchField.ChannelId => ChannelId,
        MatchField.Playlist => Playlist,
        MatchField.Title => Title,
        MatchField.Url => Url,
        _ => null,
    };

    /// <summary>Parses the output of <c>yt-dlp -J --flat-playlist</c>.</summary>
    public static MediaInfo FromJson(string url, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var isPlaylist = Str(root, "_type") == "playlist";

        JsonElement? firstEntry = null;
        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() > 0)
        {
            firstEntry = entries[0];
        }

        string? FromEntry(string name) => firstEntry is { } e ? Str(e, name) : null;

        var pageUrl = Str(root, "webpage_url") ?? Str(root, "original_url") ?? url;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in root.EnumerateObject())
        {
            if (Scalar(prop.Value) is { } value)
            {
                fields[prop.Name] = value;
            }
        }

        if (firstEntry is { } entry)
        {
            foreach (var name in OwnerFields)
            {
                if (!fields.ContainsKey(name) && Str(entry, name) is { } value)
                {
                    fields[name] = value;
                }
            }
        }

        int? count = null;
        if (root.TryGetProperty("playlist_count", out var pc) && pc.ValueKind == JsonValueKind.Number)
        {
            count = pc.GetInt32();
        }

        return new MediaInfo
        {
            Url = url,
            Title = Str(root, "title") ?? Str(root, "fulltitle"),
            Site = SiteName(Str(root, "extractor_key") ?? Str(root, "ie_key"), Str(root, "extractor")),
            Domain = NormalizeDomain(pageUrl) ?? NormalizeDomain(url),
            Channel = Str(root, "channel") ?? Str(root, "uploader") ?? FromEntry("channel") ?? FromEntry("uploader"),
            ChannelId = Str(root, "channel_id") ?? Str(root, "uploader_id") ?? FromEntry("channel_id"),
            Playlist = isPlaylist ? Str(root, "title") : Str(root, "playlist_title") ?? Str(root, "playlist"),
            IsPlaylist = isPlaylist,
            EntryCount = count,
            Fields = fields,
        };
    }

    /// <summary>
    /// The site without the sub-extractor, so videos ("Youtube"), channels/playlists ("YoutubeTab", extractor
    /// "youtube:tab") and e.g. "TwitchVod" ("twitch:vod") all route and file under one name.
    /// </summary>
    internal static string? SiteName(string? extractorKey, string? extractor)
    {
        var baseName = extractor?.Split(':')[0];
        if (string.IsNullOrEmpty(baseName))
        {
            return extractorKey ?? extractor;
        }

        if (extractorKey is not null && extractorKey.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
        {
            return extractorKey[..baseName.Length];
        }

        return extractorKey ?? baseName;
    }

    private static string? Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() is { Length: > 0 } s ? s : null,
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    public static string? NormalizeDomain(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        foreach (var prefix in new[] { "www.", "m.", "music." })
        {
            if (host.StartsWith(prefix, StringComparison.Ordinal) && host.Count(c => c == '.') > 1)
            {
                return host[prefix.Length..];
            }
        }

        return host;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
