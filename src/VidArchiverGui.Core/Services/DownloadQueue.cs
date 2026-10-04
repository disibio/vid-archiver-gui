using System.Text.RegularExpressions;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

/// <summary>
/// The download list's decisions, kept apart from the view models so they can be tested: which links pasted text
/// contains, which queued downloads to start or look up, which ones to keep for next time, and what the summary and
/// the "all done" notification say.
/// </summary>
public static partial class DownloadQueue
{
    /// <summary>
    /// The links in typed, pasted or dropped text: anything with a scheme (https://…), or a bare domain with an
    /// optional path (archive.org/details/…). Other words, such as "e.g." or the end of a sentence, are ignored.
    /// A line without a link that is one word (an id) or a yt-dlp search (examplesearch5:some words)
    /// is kept whole, since yt-dlp accepts those too and says so when it can't.
    /// </summary>
    public static IReadOnlyList<string> ExtractUrls(string text) =>
        text.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(line =>
            {
                var words = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                var links = words.Select(StripSurroundings).Where(s => SchemeUrlRegex().IsMatch(s) || BareDomainUrlRegex().IsMatch(s)).ToList();
                return links.Count == 0 && (words.Length == 1 || SearchRegex().IsMatch(line)) ? [line] : links;
            })
            .Distinct()
            .ToList();

    /// <summary>
    /// Removes quotes, brackets and sentence punctuation around a link, such as in <c>(archive.org/details/a),</c>.
    /// A closing bracket is kept when it closes one in the link, as in <c>wikipedia.org/wiki/Mercury_(planet)</c>.
    /// </summary>
    private static string StripSurroundings(string word)
    {
        word = word.TrimStart('"', '\'', '<', '(');
        while (word.Length > 0 && (word[^1] is '"' or '\'' or '>' or '.' or ',' or ';'
                                   || (word[^1] == ')' && word.Count(c => c == ')') > word.Count(c => c == '('))))
        {
            word = word[..^1];
        }

        return word;
    }

    // yt-dlp's search prefixes: <site>search:, <site>search5:, <site>searchall:, <site>searchdate:
    [GeneratedRegex(@"^[a-z0-9]+search(?:\d+|all|date)?:\S", RegexOptions.IgnoreCase)]
    private static partial Regex SearchRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*://\S+$", RegexOptions.IgnoreCase)]
    private static partial Regex SchemeUrlRegex();

    // Labels separated by dots, ending in a top-level domain of letters, then an optional port and path.
    [GeneratedRegex(@"^(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,}(?::\d+)?(?:[/?#]\S*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex BareDomainUrlRegex();

    /// <summary>
    /// How many queued downloads have their info read ahead of the ones that can start now, so the next few show
    /// their titles. Not the whole list: looking up many videos at once makes YouTube ask to sign in.
    /// </summary>
    public const int ReadAhead = 3;

    /// <summary>
    /// The queued items that fit beside the running ones under <paramref name="max"/>, in list order. Only those whose
    /// info has been read can start; the others are looked up first (<see cref="ToLookUp"/>). Nothing behind an item
    /// whose info is being read starts before it, so the downloads still go in list order when the lookups finish out
    /// of order.
    /// </summary>
    public static IReadOnlyList<T> ToStart<T>(IReadOnlyCollection<T> items, Func<T, DownloadState> state, Func<T, bool> hasInfo, int max)
    {
        var free = max - items.Count(i => state(i) == DownloadState.Downloading);
        return free <= 0
            ? []
            : items.TakeWhile(i => state(i) != DownloadState.Resolving).Where(i => state(i) == DownloadState.Queued && hasInfo(i)).Take(free).ToList();
    }

    /// <summary>
    /// The queued items to look up next, in list order: enough that the free slots under <paramref name="max"/>, plus
    /// <see cref="ReadAhead"/> more, have their info read or being read.
    /// </summary>
    public static IReadOnlyList<T> ToLookUp<T>(IReadOnlyCollection<T> items, Func<T, DownloadState> state, Func<T, bool> hasInfo, int max)
    {
        var free = Math.Max(0, max - items.Count(i => state(i) == DownloadState.Downloading));
        var lookedUp = items.Count(i => state(i) == DownloadState.Resolving || (state(i) == DownloadState.Queued && hasInfo(i)));
        return items.Where(i => state(i) == DownloadState.Queued && !hasInfo(i)).Take(free + ReadAhead - lookedUp).ToList();
    }

    /// <summary>
    /// The progress bar (0 to 1) for a playlist: the items before this one plus how far this one is, out of
    /// <paramref name="count"/>. It never goes back from <paramref name="shown"/>, the value already on the bar, because
    /// each video's separate video and audio downloads both start again from 0.
    /// </summary>
    public static double PlaylistProgress(int index, int count, double itemFraction, double shown) =>
        count <= 0 ? shown : Math.Max(shown, Math.Clamp((index - 1 + Math.Clamp(itemFraction, 0, 1)) / count, 0, 1));

    /// <summary>Whether a download is put back in the list next time: anything not done (cancelling was the user's choice).</summary>
    public static bool KeepForNextSession(DownloadState state) =>
        state is not (DownloadState.Completed or DownloadState.Skipped or DownloadState.Cancelled);

    /// <summary>
    /// The line above the list: the total, then how many are in each state (only the states that occur), so the
    /// numbers always add up to the total.
    /// </summary>
    public static string ListSummary(IReadOnlyCollection<DownloadState> states)
    {
        (DownloadState State, string Label)[] order =
        [
            (DownloadState.Downloading, "downloading"),
            (DownloadState.Queued, "queued"),
            (DownloadState.Paused, "paused"),
            (DownloadState.Resolving, "reading info"),
            (DownloadState.Ready, "ready"),
            (DownloadState.Completed, "done"),
            (DownloadState.Skipped, "already archived"),
            (DownloadState.Failed, "failed"),
            (DownloadState.Cancelled, "cancelled"),
        ];
        var parts = new List<string> { states.Count == 1 ? "1 item" : $"{states.Count} items" };
        foreach (var (state, label) in order)
        {
            if (states.Count(s => s == state) is var n and > 0)
            {
                parts.Add($"{n} {label}");
            }
        }

        return string.Join("  ·  ", parts);
    }

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
