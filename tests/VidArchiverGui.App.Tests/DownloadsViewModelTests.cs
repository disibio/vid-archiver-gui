using VidArchiverGui.App.ViewModels;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.App.Tests;

[Collection(nameof(DownloadListTests))]
public sealed class DownloadsViewModelTests : DownloadListTests
{
    [Fact]
    public Task Start_all_starts_ready_and_failed_downloads_but_not_cancelled_ones() => Headless.Run(async () =>
    {
        var ready = await AddAsync("https://fake.test/video/ready");
        var failed = await AddAsync("https://fake.test/video/broken?fail");
        var cancelled = await AddAsync("https://fake.test/video/cancelled");
        failed.StartCommand.Execute(null);
        await WaitUntilStopped(failed);
        Assert.Equal(DownloadState.Failed, failed.State);
        cancelled.State = DownloadState.Cancelled;

        _vm.StartAllCommand.Execute(null);
        await WaitUntilStopped(ready);
        await WaitUntilStopped(failed);

        Assert.Equal(DownloadState.Completed, ready.State);
        Assert.Equal(2, Runs(failed)); // tried again
        Assert.Equal(DownloadState.Cancelled, cancelled.State);
        Assert.True(File.Exists(Path.Combine(_folder, "ready.mkv")));
    });

    [Fact]
    public Task A_playlist_bar_shows_the_whole_playlist_and_never_goes_back() => Headless.Run(async () =>
    {
        var item = await AddAsync("https://fake.test/playlist/3");
        var values = new List<double>();
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(item.Progress) && item.State == DownloadState.Downloading)
            {
                values.Add(item.Progress);
            }
        };

        item.StartCommand.Execute(null);
        await Headless.WaitUntil(() => item.State == DownloadState.Completed, "it downloads");

        Assert.Equal(values.Order(), values);
        Assert.Contains(values, v => v is > 0 and < 100.0 / 3); // part way through the first item
        Assert.Contains(values, v => v is > 100.0 / 3 and < 200.0 / 3);
        Assert.Contains(values, v => v is > 200.0 / 3 and < 100);
        Assert.Equal(100, values[^1], 6);
    });

    [Fact]
    public Task Open_folder_selects_the_downloaded_file_while_it_exists() => Headless.Run(async () =>
    {
        var item = await AddAsync("https://fake.test/playlist/2");
        await item.OpenFolderCommand.ExecuteAsync(null);
        Assert.Equal(_folder, _t.Dialogs.Opened[^1]); // nothing downloaded yet

        item.StartCommand.Execute(null);
        await Headless.WaitUntil(() => item.State == DownloadState.Completed, "it downloads");
        await item.OpenFolderCommand.ExecuteAsync(null);
        var last = Path.Combine(_folder, "Fake video 2.mkv");
        Assert.Equal(last, _t.Dialogs.Opened[^1]);

        File.Delete(last);
        await item.OpenFolderCommand.ExecuteAsync(null);
        Assert.Equal(_folder, _t.Dialogs.Opened[^1]);
    });

    [Fact]
    public Task Pausing_stops_the_download_and_resume_carries_on_from_the_partial_file() => Headless.Run(() => Slowly(async () =>
    {
        var item = await AddAsync("https://fake.test/video/pausable");
        item.StartCommand.Execute(null);
        await Headless.WaitUntil(() => item.Progress >= 30, "it's part way");

        item.PauseCommand.Execute(null);
        await Headless.WaitUntil(() => item.State == DownloadState.Paused, "it pauses");
        Assert.Equal("Resume", item.StartText);
        Assert.Equal(0, _vm.ActiveCount); // no "quit while downloading?" for it
        Assert.True(File.Exists(Path.Combine(_folder, "pausable.f1.mp4.part")));

        item.StartCommand.Execute(null);
        await Headless.WaitUntil(() => item.State == DownloadState.Completed, "it finishes");
        Assert.Contains("Resuming download at byte", item.LogText);
        Assert.True(File.Exists(Path.Combine(_folder, "pausable.mkv")));
    }));

    [Fact]
    public Task A_paused_queued_download_stays_paused_when_a_slot_frees_up() => Headless.Run(() => Slowly(async () =>
    {
        _t.Settings.MaxConcurrentDownloads = 1;
        var first = await AddAsync("https://fake.test/video/first");
        var second = await AddAsync("https://fake.test/video/second");
        _vm.StartAllCommand.Execute(null);
        Assert.Equal(DownloadState.Queued, second.State);

        second.PauseCommand.Execute(null);
        Assert.Equal(DownloadState.Paused, second.State);
        Assert.Contains("1 paused", _vm.Summary);

        await WaitUntilStopped(first);
        Assert.Equal(DownloadState.Completed, first.State);
        Assert.Equal(DownloadState.Paused, second.State);
    }));

    [Fact]
    public Task Pause_all_pauses_running_and_queued_downloads_and_start_all_resumes_them() => Headless.Run(() => Slowly(async () =>
    {
        _t.Settings.MaxConcurrentDownloads = 1;
        var first = await AddAsync("https://fake.test/video/one");
        var second = await AddAsync("https://fake.test/video/two");
        _vm.StartAllCommand.Execute(null);
        await Headless.WaitUntil(() => first.Progress > 0, "the first is downloading");

        _vm.PauseAllCommand.Execute(null);
        await Headless.WaitUntil(() => first.State == DownloadState.Paused, "the first pauses");
        Assert.Equal(DownloadState.Paused, second.State);

        _vm.StartAllCommand.Execute(null);
        await Headless.WaitUntil(() => first.State == DownloadState.Completed && second.State == DownloadState.Completed, "both finish", 60);
    }));

    [Fact]
    public Task A_paused_download_is_still_paused_after_a_restart() => Headless.Run(async () =>
    {
        var item = await AddAsync("https://fake.test/video/later");
        item.StartCommand.Execute(null);
        item.PauseCommand.Execute(null); // queued for a moment, or already downloading
        await Headless.WaitUntil(() => item.State is DownloadState.Paused or DownloadState.Completed, "it pauses");
        Assert.Equal(DownloadState.Paused, item.State);

        _vm.SaveUnfinished();
        Assert.True(Assert.Single(_t.Settings.UnfinishedDownloads).Paused);

        var next = new DownloadsViewModel(_t.Host);
        next.RestoreUnfinished();
        next.ResumeRestored();
        var restored = Assert.Single(next.Items);
        await Headless.WaitUntil(() => restored.State != DownloadState.Resolving, "its info is read");
        Assert.Equal(DownloadState.Paused, restored.State);
    });

    [Fact]
    public Task Resuming_a_restored_paused_download_whose_info_failed_downloads_it() => Headless.Run(async () =>
    {
        _t.Settings.UnfinishedDownloads = [new SavedDownload { Url = "https://fake.test/video/offline", Paused = true }];
        _vm.RestoreUnfinished();
        var item = Assert.Single(_vm.Items);
        Environment.SetEnvironmentVariable("FAKEYTDLP_INFO_FAIL", "1");
        try
        {
            _vm.ResumeRestored();
            await Headless.WaitUntil(() => item.State != DownloadState.Resolving, "its info is read");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKEYTDLP_INFO_FAIL", null);
        }

        Assert.Equal(DownloadState.Failed, item.State);
        _vm.SaveUnfinished();
        Assert.True(Assert.Single(_t.Settings.UnfinishedDownloads).Paused); // quitting now still keeps it paused

        item.StartCommand.Execute(null);
        await Headless.WaitUntil(() => item.State is DownloadState.Completed or DownloadState.Paused, "it downloads");
        Assert.Equal(DownloadState.Completed, item.State);
    });

    [Fact]
    public Task A_failed_download_is_put_back_as_failed_without_reading_its_info() => Headless.Run(async () =>
    {
        var item = await AddAsync("https://fake.test/video/broken?fail");
        item.StartCommand.Execute(null);
        await WaitUntilStopped(item);
        _vm.SaveUnfinished();

        var next = new DownloadsViewModel(_t.Host);
        next.RestoreUnfinished();
        next.ResumeRestored();
        var restored = Assert.Single(next.Items);
        Assert.Equal(DownloadState.Failed, restored.State);
        Assert.Equal(item.Error, restored.Error);
        Assert.Null(restored.Info);

        restored.StartCommand.Execute(null); // reads its info, then tries again
        await Headless.WaitUntil(() => restored.Info is not null, "its info is read");
    });

    [Fact]
    public Task Clear_finished_also_clears_cancelled_downloads_but_not_failed_ones() => Headless.Run(async () =>
    {
        var done = await AddAsync("https://fake.test/video/done");
        var cancelled = await AddAsync("https://fake.test/video/cancelled");
        var failed = await AddAsync("https://fake.test/video/failed");
        done.State = DownloadState.Completed;
        cancelled.State = DownloadState.Cancelled;
        failed.State = DownloadState.Failed;

        _vm.ClearFinishedCommand.Execute(null);

        Assert.Equal([failed], _vm.Items);
    });

    [Fact]
    public Task Pause_all_also_pauses_downloads_whose_info_is_still_being_read() => Headless.Run(async () =>
    {
        _t.Settings.AutoStartDownloads = true;
        _t.Settings.UnfinishedDownloads = [new SavedDownload { Url = "https://fake.test/video/waiting" }];
        _vm.RestoreUnfinished();
        var item = Assert.Single(_vm.Items);
        Assert.True(item.CanPause);

        _vm.PauseAllCommand.Execute(null);
        Assert.False(item.CanPause);
        _vm.SaveUnfinished();
        Assert.True(Assert.Single(_t.Settings.UnfinishedDownloads).Paused);

        _vm.ResumeRestored();
        await Headless.WaitUntil(() => item.State != DownloadState.Resolving, "its info is read");
        await Task.Delay(500); // long enough for an auto-start to show
        Assert.Equal(DownloadState.Paused, item.State);
    });

    [Fact]
    public Task A_finished_download_shows_the_thumbnail_saved_beside_it() => Headless.Run(async () =>
    {
        _t.Settings.DefaultPreset.Arguments = "--write-thumbnail";
        var item = await AddAsync("https://fake.test/video/pictured");
        item.StartCommand.Execute(null);
        await Headless.WaitUntil(() => item.Thumbnail is not null, "the thumbnail shows");
    });

    [Fact]
    public Task Without_a_thumbnail_file_there_is_none() => Headless.Run(async () =>
    {
        var item = await AddAsync("https://fake.test/video/plain");
        item.StartCommand.Execute(null);
        await Headless.WaitUntil(() => item.State == DownloadState.Completed, "it downloads");
        await Task.Delay(1000); // FakeYtDlp's file has no embedded cover either, if ffmpeg is here to look
        Assert.Null(item.Thumbnail);
        Assert.DoesNotContain("thumbnail", item.LogText);
    });

    [Fact]
    public Task Starting_a_download_whose_info_failed_reads_it_again_and_then_downloads() => Headless.Run(async () =>
    {
        DownloadItemViewModel item;
        Environment.SetEnvironmentVariable("FAKEYTDLP_INFO_FAIL", "1");
        try
        {
            item = await AddAsync("https://fake.test/video/later");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKEYTDLP_INFO_FAIL", null);
        }

        Assert.Equal(DownloadState.Failed, item.State);
        Assert.False(_t.Settings.AutoStartDownloads);

        item.StartCommand.Execute(null);
        await Headless.WaitUntil(() => item.State == DownloadState.Completed, "it downloads");
    });
}
