using VidArchiverGui.App.ViewModels;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.App.Tests;

/// <summary>The download list, end to end, with FakeYtDlp standing in for yt-dlp (no network).</summary>
public sealed class DownloadsViewModelTests : IDisposable
{
    private readonly TestHost _t = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "vidarchivergui-downloads-" + Guid.NewGuid().ToString("N"));
    private readonly DownloadsViewModel _vm;

    public DownloadsViewModelTests()
    {
        var fake = new Downloader
        {
            Name = "Fake",
            ExecutablePath = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "FakeYtDlp.exe" : "FakeYtDlp"),
        };
        _t.Settings.Downloaders.Add(fake);
        _t.Settings.DefaultDownloaderId = fake.Id;
        _t.Settings.AutoFallback = false; // never install the real downloaders

        // The default presets write to the real archive file; downloads here go to a scratch folder instead.
        var preset = new Preset { Name = "Test" };
        _t.Settings.Presets.Add(preset);
        _t.Settings.DefaultPresetId = preset.Id;
        _t.Settings.Rules.Clear();
        _t.Settings.FallbackDestination = _folder;

        _vm = new DownloadsViewModel(_t.Host);
    }

    public void Dispose()
    {
        _vm.CancelEverything();
        _t.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private async Task<DownloadItemViewModel> AddAsync(string url)
    {
        Assert.Equal(1, _vm.AddUrls(url));
        var item = _vm.Items[^1];
        await Headless.WaitUntil(() => item.State != DownloadState.Resolving, "its info is read");
        return item;
    }

    private static Task WaitUntilStopped(DownloadItemViewModel item) =>
        Headless.WaitUntil(() => item.State is not (DownloadState.Queued or DownloadState.Downloading or DownloadState.Resolving), "it stops");

    private static int Runs(DownloadItemViewModel item) => item.LogText.Split('\n').Count(l => l.StartsWith("> ", StringComparison.Ordinal));

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
