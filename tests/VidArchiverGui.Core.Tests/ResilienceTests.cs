using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

public class ResilienceTests
{
    [Theory]
    [InlineData("[youtube] jNQXAC9IVRw: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies for the authentication.", FailureKind.NeedsLogin)]
    [InlineData("[youtube] abc: Sign in to confirm your age. This video may be inappropriate for some users.", FailureKind.NeedsLogin)]
    [InlineData("[youtube] abc: Join this channel to get access to members-only content like this video", FailureKind.NeedsLogin)]
    [InlineData("[youtube] abc: Private video. Sign in if you've been granted access to this video", FailureKind.Unavailable)]
    [InlineData("[youtube] abc: Video unavailable. This video has been removed by the uploader", FailureKind.Unavailable)]
    [InlineData("Unsupported URL: https://example.com/", FailureKind.Unavailable)]
    [InlineData("[youtube] abc: Video unavailable. This content isn't available, try again later.", FailureKind.RateLimited)]
    [InlineData("[youtube] abc: Unable to download API page: HTTP Error 429: Too Many Requests", FailureKind.RateLimited)]
    [InlineData("[youtube] abc: Unable to download webpage: <urlopen error [Errno 11001] getaddrinfo failed>", FailureKind.Network)]
    [InlineData("unable to download video data: HTTP Error 503: Service Unavailable", FailureKind.Network)]
    [InlineData("[youtube] abc: Unable to extract initial player response; please report this issue on https://github.com/yt-dlp/yt-dlp/issues", FailureKind.SiteChanged)]
    [InlineData("[youtube] abc: Requested format is not available. Use --list-formats for a list of available formats", FailureKind.SiteChanged)]
    [InlineData("[vimeo] 123: something new and unexpected", FailureKind.SiteChanged)]
    [InlineData("Could not copy Chrome cookie database. See  https://github.com/yt-dlp/yt-dlp/issues/7271  for more info", FailureKind.CookiesUnreadable)]
    [InlineData("Failed to decrypt with DPAPI. See  https://github.com/yt-dlp/yt-dlp/issues/10927  for more info", FailureKind.CookiesUnreadable)]
    [InlineData("Postprocessing: Conversion failed!", FailureKind.Unknown)]
    [InlineData("", FailureKind.Unknown)]
    public void Classifies_real_error_messages(string message, FailureKind expected) =>
        Assert.Equal(expected, Resilience.Classify(message));

    private static Engine Custom(string name, string exe, EngineFlavor flavor = EngineFlavor.YtDlp) =>
        new() { Id = name, Name = name, ExecutablePath = exe, Flavor = flavor };

    private sealed class Fixture : IDisposable
    {
        public readonly string Exe = Path.GetTempFileName();
        public readonly AppSettings Settings = new();
        public readonly ToolManager Tools;
        public readonly List<string> Calls = [];
        public readonly List<string> Status = [];

        public Fixture() => Tools = new ToolManager(Settings);

        public Task<FallbackResult> Run(Engine primary, bool fallback, params string?[] results)
        {
            var queue = new Queue<string?>(results);
            Settings.AutoFallback = fallback;
            return Resilience.RunAsync(Tools, Settings, primary,
                (engine, _) =>
                {
                    Calls.Add(engine.Engine.Name);
                    return Task.FromResult(queue.Dequeue());
                },
                Status.Add, CancellationToken.None, [TimeSpan.Zero, TimeSpan.Zero]);
        }

        public void Dispose() => File.Delete(Exe);
    }

    [Fact]
    public void Fallbacks_are_other_usable_downloaders_of_the_same_flavor()
    {
        using var f = new Fixture();
        Engine a = Custom("a", f.Exe), b = Custom("b", f.Exe), ytdl = Custom("ytdl", f.Exe, EngineFlavor.YoutubeDl),
            missing = Custom("missing", Path.Combine(Path.GetTempPath(), "does-not-exist.exe"));
        foreach (var e in new[] { a, ytdl, missing, b })
        {
            f.Settings.Engines.Add(e);
        }

        Assert.Equal([b], Resilience.FallbackEngines(f.Settings, a));
        Assert.Empty(Resilience.FallbackEngines(f.Settings, ytdl));
    }

    [Fact]
    public async Task Site_changes_move_on_to_the_next_downloader()
    {
        using var f = new Fixture();
        Engine a = Custom("a", f.Exe), b = Custom("b", f.Exe);
        f.Settings.Engines.Add(a);
        f.Settings.Engines.Add(b);

        var result = await f.Run(a, fallback: true, "[youtube] x: Unable to extract nsig function code", null);

        Assert.Null(result.Error);
        Assert.Same(b, result.Engine);
        Assert.Equal(["a", "b"], f.Calls);
    }

    [Fact]
    public async Task Fallback_can_be_turned_off_and_reports_every_downloader_tried()
    {
        using var f = new Fixture();
        Engine a = Custom("a", f.Exe), b = Custom("b", f.Exe);
        f.Settings.Engines.Add(a);
        f.Settings.Engines.Add(b);
        const string broken = "[youtube] x: Unable to extract nsig function code";

        var off = await f.Run(a, fallback: false, broken);
        Assert.Equal(broken, off.Error);
        Assert.Equal(["a"], f.Calls);

        f.Calls.Clear();
        var all = await f.Run(a, fallback: true, broken, broken);
        Assert.Equal(FailureKind.SiteChanged, all.Kind);
        Assert.EndsWith("(tried a, b)", all.Error);
    }

    [Fact]
    public async Task Network_errors_retry_the_same_downloader_then_give_up()
    {
        using var f = new Fixture();
        Engine a = Custom("a", f.Exe), b = Custom("b", f.Exe);
        f.Settings.Engines.Add(a);
        f.Settings.Engines.Add(b);
        const string net = "Unable to download webpage: The read operation timed out";

        var recovered = await f.Run(a, fallback: true, net, null);
        Assert.Null(recovered.Error);
        Assert.Equal(["a", "a"], f.Calls);

        f.Calls.Clear();
        var failed = await f.Run(a, fallback: true, net, net, net);
        Assert.Equal(FailureKind.Network, failed.Kind);
        Assert.Equal(["a", "a", "a"], f.Calls); // no point switching downloader for a network problem
    }

    [Theory]
    [InlineData("[youtube] x: Sign in to confirm you're not a bot", FailureKind.NeedsLogin)]
    [InlineData("[youtube] x: Private video", FailureKind.Unavailable)]
    [InlineData("HTTP Error 429: Too Many Requests", FailureKind.RateLimited)]
    public async Task Errors_another_downloader_cant_fix_stop_right_away(string error, FailureKind kind)
    {
        using var f = new Fixture();
        Engine a = Custom("a", f.Exe), b = Custom("b", f.Exe);
        f.Settings.Engines.Add(a);
        f.Settings.Engines.Add(b);

        var result = await f.Run(a, fallback: true, error);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(["a"], f.Calls);
    }
}
