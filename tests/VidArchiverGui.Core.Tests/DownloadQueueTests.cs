using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

public class DownloadQueueTests
{
    private static IReadOnlyList<int> ToStart(int max, params DownloadState[] states) =>
        DownloadQueue.ToStart(Enumerable.Range(0, states.Length).ToList(), i => states[i], max);

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
