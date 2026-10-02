namespace VidArchiverGui.Core.Models;

/// <summary>
/// A download that hadn't finished when the app was closed, so it can be put back in the list next time.
/// Only the user's choices are kept; the video's info is looked up again.
/// </summary>
public sealed record SavedDownload
{
    public required string Url { get; init; }
    public string? PresetId { get; init; }
    public string? CookieId { get; init; }
    public string? DownloaderId { get; init; }

    /// <summary>The folder, if the user picked it by hand (a rule's folder is worked out again).</summary>
    public string? Destination { get; init; }

    /// <summary>It was paused, so it stays paused rather than becoming ready (or starting) when it's put back.</summary>
    public bool Paused { get; init; }

    /// <summary>Why it failed, so it's put back as failed rather than looked up again on every start.</summary>
    public string? Error { get; init; }
}
