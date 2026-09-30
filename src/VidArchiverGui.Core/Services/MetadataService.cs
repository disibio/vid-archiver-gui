using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

/// <summary>Reads just enough information about a URL (site, channel, playlist...) to route it.</summary>
public static class MetadataService
{
    /// <param name="cookieArgs">Cookie options chosen in the app; when present they replace any cookie options in the preset.</param>
    public static async Task<MediaInfo> FetchAsync(string url, IReadOnlyList<string> presetArgs, ResolvedEngine engine,
        IReadOnlyList<string>? cookieArgs = null, CancellationToken ct = default)
    {
        // --flat-playlist + a single item keeps channel/playlist lookups fast. youtube-dl supports all of these too.
        List<string> args =
        [
            "-J", "--flat-playlist", "--playlist-items", "1", "--no-warnings", "--encoding", "utf-8",
            .. ArgumentParser.ExtractAccessOptions(ArgumentParser.WithCookies(presetArgs, cookieArgs ?? [])),
            .. engine.ExtraArgs,
            "--", url,
        ];

        var result = await ProcessHelper.RunAsync(engine.Path, args, ct);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            throw new YtDlpException(YtDlpOutputParser.LastError(result.StdErr) ?? $"{engine.Engine.Name} exited with code {result.ExitCode}");
        }

        return MediaInfo.FromJson(url, result.StdOut);
    }
}
