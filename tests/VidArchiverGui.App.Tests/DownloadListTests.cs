using VidArchiverGui.App.ViewModels;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.App.Tests;

/// <summary>
/// A download list with FakeYtDlp standing in for yt-dlp (no network), downloading into a scratch folder. Tests using
/// it share one collection, so they don't run at the same time: FakeYtDlp takes settings from environment variables.
/// </summary>
public abstract class DownloadListTests : IDisposable
{
    protected readonly TestHost _t = new();
    protected readonly string _folder = Path.Combine(Path.GetTempPath(), "vidarchivergui-downloads-" + Guid.NewGuid().ToString("N"));
    protected readonly DownloadsViewModel _vm;

    protected DownloadListTests()
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

    protected async Task<DownloadItemViewModel> AddAsync(string url)
    {
        Assert.Equal(1, _vm.AddUrls(url));
        var item = _vm.Items[^1];
        await Headless.WaitUntil(() => item.State != DownloadState.Resolving, "its info is read");
        return item;
    }

    protected static Task WaitUntilStopped(DownloadItemViewModel item) =>
        Headless.WaitUntil(() => item.State is not (DownloadState.Queued or DownloadState.Downloading or DownloadState.Resolving), "it stops");

    /// <summary>Makes FakeYtDlp take about 2 seconds per video instead of a fraction of one, so there's time to act mid-download.</summary>
    protected static async Task Slowly(Func<Task> test)
    {
        Environment.SetEnvironmentVariable("FAKEYTDLP_STEP_MS", "100");
        try
        {
            await test();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKEYTDLP_STEP_MS", null);
        }
    }

    protected static int Runs(DownloadItemViewModel item) => item.LogText.Split('\n').Count(l => l.StartsWith("> ", StringComparison.Ordinal));
}
