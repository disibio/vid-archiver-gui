using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

/// <summary>
/// The download list's decisions, kept apart from the view models so they can be tested: which queued downloads to
/// start, which ones to keep for next time, and what the "all done" notification says.
/// </summary>
public static class DownloadQueue
{
    /// <summary>The queued items that fit beside the running ones under <paramref name="max"/>, in list order.</summary>
    public static IReadOnlyList<T> ToStart<T>(IReadOnlyCollection<T> items, Func<T, DownloadState> state, int max)
    {
        var free = max - items.Count(i => state(i) == DownloadState.Downloading);
        return free <= 0 ? [] : items.Where(i => state(i) == DownloadState.Queued).Take(free).ToList();
    }

    /// <summary>Whether a download is put back in the list next time: anything not done (cancelling was the user's choice).</summary>
    public static bool KeepForNextSession(DownloadState state) =>
        state is not (DownloadState.Completed or DownloadState.Skipped or DownloadState.Cancelled);

    /// <summary>The "all done" notification's text for downloads that ended since the queue was last empty.</summary>
    public static string BatchSummary(string firstTitle, int done, int skipped, int failed)
    {
        if (done + skipped + failed == 1)
        {
            return (done == 1 ? "Finished: " : skipped == 1 ? "Already in the archive: " : "Failed: ") + firstTitle;
        }

        var parts = new List<string>();
        if (done > 0)
        {
            parts.Add($"{done} finished");
        }

        if (skipped > 0)
        {
            parts.Add($"{skipped} already in the archive");
        }

        if (failed > 0)
        {
            parts.Add($"{failed} failed");
        }

        return string.Join(", ", parts) + ".";
    }
}
