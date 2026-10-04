using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

public class DownloadQueueTests
{
    [Theory]
    [InlineData(1, 4, 0.0, 0.0, 0.0)]
    [InlineData(1, 4, 0.5, 0.0, 0.125)]
    [InlineData(3, 4, 0.5, 0.0, 0.625)]
    [InlineData(4, 4, 1.0, 0.0, 1.0)]
    [InlineData(2, 4, 0.0, 0.4, 0.4)] // the audio of item 2 starting at 0 doesn't move the bar back
    [InlineData(2, 4, 2.0, 0.0, 0.5)] // a bad fraction can't push past the item
    [InlineData(9, 4, 1.0, 0.0, 1.0)]
    [InlineData(1, 0, 0.5, 0.3, 0.3)]
    public void Playlist_progress_counts_finished_items_and_never_goes_back(int index, int count, double fraction, double shown, double expected)
    {
        Assert.Equal(expected, DownloadQueue.PlaylistProgress(index, count, fraction, shown), 6);
    }

    [Fact]
    public void Extracts_links_and_ignores_other_words()
    {
        const string text = """
            Watch this: https://archive.org/details/apollo-11. Also (commons.wikimedia.org/wiki/File:Moon.jpg), e.g. this one.
            "https://images.nasa.gov/details/as11-40-5875", <https://archive.org/search?query=nasa&sort=date> and https://archive.org/details/apollo-11 again
            localhost:8080/video Mr. Smith v1.2 ... (https://en.wikipedia.org/wiki/Mercury_(planet)).
            """;
        Assert.Equal(
            [
                "https://archive.org/details/apollo-11", "commons.wikimedia.org/wiki/File:Moon.jpg", "https://images.nasa.gov/details/as11-40-5875",
                "https://archive.org/search?query=nasa&sort=date", "https://en.wikipedia.org/wiki/Mercury_(planet)",
            ],
            DownloadQueue.ExtractUrls(text));
    }

    [Fact]
    public void A_line_with_just_one_word_or_a_search_goes_to_the_downloader_as_it_is()
    {
        const string text = """
              abcDEF12345
            examplesearch5:some words
            The information is here
            example:abcDEF12345
            """;
        Assert.Equal(["abcDEF12345", "examplesearch5:some words", "example:abcDEF12345"], DownloadQueue.ExtractUrls(text));
    }

    [Fact]
    public void Summary_counts_every_state_that_occurs_so_they_add_up()
    {
        Assert.Equal("0 items", DownloadQueue.ListSummary([]));
        Assert.Equal("1 item  ·  1 ready", DownloadQueue.ListSummary([DownloadState.Ready]));
        Assert.Equal("6 items  ·  1 downloading  ·  2 ready  ·  1 already archived  ·  1 failed  ·  1 cancelled",
            DownloadQueue.ListSummary([
                DownloadState.Ready, DownloadState.Failed, DownloadState.Downloading,
                DownloadState.Skipped, DownloadState.Cancelled, DownloadState.Ready,
            ]));
    }

    private static IReadOnlyList<int> ToStart(int max, params DownloadState[] states) =>
        DownloadQueue.ToStart(Enumerable.Range(0, states.Length).ToList(), i => states[i], _ => true, max);

    private static IReadOnlyList<int> ToLookUp(int max, params (DownloadState State, bool HasInfo)[] items) =>
        DownloadQueue.ToLookUp(Enumerable.Range(0, items.Length).ToList(), i => items[i].State, i => items[i].HasInfo, max);

    [Fact]
    public void Starts_queued_downloads_in_list_order_up_to_the_limit()
    {
        Assert.Equal([1, 3], ToStart(3,
            DownloadState.Downloading, DownloadState.Queued, DownloadState.Ready, DownloadState.Queued, DownloadState.Queued));
    }

    [Fact]
    public void Starts_nothing_when_the_limit_is_reached_or_lowered_below_what_runs()
    {
        Assert.Empty(ToStart(1, DownloadState.Downloading, DownloadState.Queued));
        Assert.Empty(ToStart(1, DownloadState.Downloading, DownloadState.Downloading, DownloadState.Queued));
    }

    [Fact]
    public void A_queued_download_starts_only_once_its_info_is_read()
    {
        var hasInfo = new[] { false, true };
        Assert.Equal([1], DownloadQueue.ToStart([0, 1], _ => DownloadState.Queued, i => hasInfo[i], 1));
    }

    [Fact]
    public void A_download_whose_info_comes_back_first_waits_for_the_one_above_it()
    {
        Assert.Empty(ToStart(1, DownloadState.Resolving, DownloadState.Queued));
        Assert.Equal([1], ToStart(2, DownloadState.Completed, DownloadState.Queued, DownloadState.Resolving, DownloadState.Queued));
    }

    [Fact]
    public void Looks_up_the_free_slots_and_three_more_not_the_whole_list()
    {
        var queued = Enumerable.Repeat((DownloadState.Queued, false), 10).ToArray();
        Assert.Equal([0, 1, 2, 3], ToLookUp(1, queued));
        Assert.Equal([0, 1, 2, 3, 4], ToLookUp(2, queued));
    }

    [Fact]
    public void Looks_up_only_what_keeps_three_ahead_of_the_running_downloads()
    {
        Assert.Equal([4], ToLookUp(1,
            (DownloadState.Downloading, true), (DownloadState.Queued, true), (DownloadState.Resolving, false),
            (DownloadState.Ready, false), (DownloadState.Queued, false), (DownloadState.Queued, false)));
        Assert.Empty(ToLookUp(1,
            (DownloadState.Downloading, true), (DownloadState.Queued, true), (DownloadState.Queued, true),
            (DownloadState.Resolving, false), (DownloadState.Queued, false)));
    }

    [Theory]
    [InlineData(DownloadState.Resolving, true)]
    [InlineData(DownloadState.Ready, true)]
    [InlineData(DownloadState.Queued, true)]
    [InlineData(DownloadState.Downloading, true)]
    [InlineData(DownloadState.Failed, true)]
    [InlineData(DownloadState.Completed, false)]
    [InlineData(DownloadState.Skipped, false)]
    [InlineData(DownloadState.Cancelled, false)]
    public void Keeps_only_unfinished_downloads_for_next_time(DownloadState state, bool keep) =>
        Assert.Equal(keep, DownloadQueue.KeepForNextSession(state));

    [Theory]
    [InlineData(1, 0, 0, "Finished: Video")]
    [InlineData(0, 1, 0, "Already in the archive: Video")]
    [InlineData(0, 0, 1, "Failed: Video")]
    [InlineData(3, 1, 2, "3 finished, 1 already in the archive, 2 failed.")]
    [InlineData(2, 0, 0, "2 finished.")]
    public void Batch_summary_names_a_single_download_and_counts_several(int done, int skipped, int failed, string expected) =>
        Assert.Equal(expected, DownloadQueue.BatchSummary("Video", done, skipped, failed));
}
