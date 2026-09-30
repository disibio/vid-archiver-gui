using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

/// <summary>Tool downloads against a local server that can send slowly, stop sending, or be cancelled mid-way.</summary>
public sealed class ToolDownloadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vidarchivergui-tests-").FullName;
    private readonly HttpListener _server = new();
    private readonly string _url;
    private readonly byte[] _content = RandomNumberGenerator.GetBytes(5 * 100_000);

    public ToolDownloadTests()
    {
        var port = FreePort();
        _url = $"http://127.0.0.1:{port}/tool.bin";
        _server.Prefixes.Add($"http://127.0.0.1:{port}/");
        _server.Start();
    }

    public void Dispose()
    {
        _server.Close();
        Directory.Delete(_dir, recursive: true);
    }

    private string Sha => Convert.ToHexStringLower(SHA256.HashData(_content));
    private string Destination => Path.Combine(_dir, "tool");

    /// <summary>Serves the content in 5 chunks, pausing <paramref name="gap"/> before each; stops after <paramref name="chunks"/>.</summary>
    private async Task ServeAsync(TimeSpan gap, int chunks = 5)
    {
        var ctx = await _server.GetContextAsync();
        ctx.Response.ContentLength64 = _content.Length;
        try
        {
            for (var i = 0; i < 5; i++)
            {
                if (i >= chunks)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3)); // go quiet
                    // Drop the connection: Close() on a short response throws on Windows (http.sys).
                    ctx.Response.Abort();
                    return;
                }
                await Task.Delay(gap);
                await ctx.Response.OutputStream.WriteAsync(_content.AsMemory(i * 100_000, 100_000));
                await ctx.Response.OutputStream.FlushAsync();
            }
            ctx.Response.Close();
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException) { }
    }

    [Fact]
    public async Task A_slow_download_that_keeps_moving_is_not_cut_off()
    {
        // 5 × 400 ms = 2 s in total, longer than the 1 s stall limit; each gap is shorter, so it must finish.
        var server = ServeAsync(TimeSpan.FromMilliseconds(400));
        var reports = new List<TransferProgress>();

        await ReleaseDownloader.DownloadFileAsync(_url, Destination, new SyncProgress(reports.Add), CancellationToken.None, Sha, TimeSpan.FromSeconds(1));

        Assert.Equal(_content, File.ReadAllBytes(Destination));
        Assert.True(reports[0].Connecting);
        Assert.Equal(_content.Length, reports[^1].Received);
        Assert.Equal(_content.Length, reports[^1].Total);
        Assert.Equal(1.0, reports[^1].Fraction);
        await server;
    }

    [Fact]
    public async Task A_download_that_stops_sending_gives_up_with_a_clear_message()
    {
        var server = ServeAsync(TimeSpan.Zero, chunks: 2);

        var e = await Assert.ThrowsAsync<TimeoutException>(() =>
            ReleaseDownloader.DownloadFileAsync(_url, Destination, null, CancellationToken.None, Sha, TimeSpan.FromSeconds(1)));

        Assert.Contains("no data arrived for 1 seconds", e.Message);
        Assert.False(File.Exists(Destination));
        Assert.False(File.Exists(Destination + ".download"));
        _server.Close();
        await server;
    }

    [Fact]
    public async Task Cancelling_stops_the_download_and_leaves_nothing_behind()
    {
        var server = ServeAsync(TimeSpan.FromMilliseconds(300));
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Received > 0)
            {
                cts.Cancel(); // the user pressed Cancel once data was flowing
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReleaseDownloader.DownloadFileAsync(_url, Destination, progress, cts.Token, Sha, TimeSpan.FromMinutes(1)));

        Assert.False(File.Exists(Destination));
        Assert.False(File.Exists(Destination + ".download"));
        _server.Close();
        await server;
    }

    [Theory]
    [InlineData(0, null, 0, true, "Connecting to download tool.bin…")]
    [InlineData(1_572_864, 3_145_728L, 524_288, false, "Downloading tool.bin: 1.5 of 3.0 MB (0.5 MB/s)")]
    [InlineData(1_572_864, null, 0, false, "Downloading tool.bin: 1.5 MB")]
    public void Progress_text(long received, long? total, double speed, bool connecting, string expected)
    {
        var text = new TransferProgress("tool.bin", received, total, speed) { Connecting = connecting }.ToString();
        Assert.Equal(expected, text.Replace(',', '.'));
    }

    private sealed class SyncProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
