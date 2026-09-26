using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

public class ParsingTests
{
    [Fact]
    public void Parses_single_video_json()
    {
        const string json = """
            {"id":"dQw4w9WgXcQ","title":"Never Gonna","channel":"Rick Astley","channel_id":"UCuAXFkgsw1L7xaCfnd5JJOw",
             "uploader":"Rick Astley","extractor_key":"Youtube","webpage_url":"https://www.youtube.com/watch?v=dQw4w9WgXcQ","duration":212}
            """;
        var info = MediaInfo.FromJson("https://youtu.be/dQw4w9WgXcQ", json);

        Assert.False(info.IsPlaylist);
        Assert.Equal("Youtube", info.Site);
        Assert.Equal("youtube.com", info.Domain);
        Assert.Equal("Rick Astley", info.Channel);
        Assert.Equal("UCuAXFkgsw1L7xaCfnd5JJOw", info.ChannelId);
        Assert.Null(info.Playlist);
        Assert.Equal("212", info.Fields["duration"]);
    }

    [Fact]
    public void Parses_flat_playlist_json_and_falls_back_to_first_entry_channel()
    {
        const string json = """
            {"_type":"playlist","id":"PL1","title":"Lo-fi Beats","extractor_key":"YoutubeTab","extractor":"youtube:tab","playlist_count":42,
             "webpage_url":"https://music.youtube.com/playlist?list=PL1",
             "entries":[{"_type":"url","id":"x","channel":"Chill Guy","channel_id":"UC1"}]}
            """;
        var info = MediaInfo.FromJson("u", json);

        Assert.True(info.IsPlaylist);
        Assert.Equal("Youtube", info.Site);
        Assert.Equal("YoutubeTab", info.Fields["extractor_key"]);
        Assert.Equal("Lo-fi Beats", info.Playlist);
        Assert.Equal("Chill Guy", info.Channel);
        Assert.Equal(42, info.EntryCount);
        Assert.Equal("youtube.com", info.Domain);
        // Owner fields missing at the top level come from the first entry; its title/id do not.
        Assert.Equal("UC1", info.Fields["channel_id"]);
        Assert.Equal("Lo-fi Beats", info.Fields["title"]);
        Assert.Equal("42", info.Fields["playlist_count"]);
    }

    [Theory]
    [InlineData("Youtube", "youtube", "Youtube")]
    [InlineData("YoutubeTab", "youtube:tab", "Youtube")]
    [InlineData("TwitchVod", "twitch:vod", "Twitch")]
    [InlineData("Soundcloud", "soundcloud", "Soundcloud")]
    [InlineData("Generic", null, "Generic")]
    public void Site_name_drops_sub_extractor(string key, string? extractor, string expected)
    {
        Assert.Equal(expected, MediaInfo.SiteName(key, extractor));
    }

    [Fact]
    public void Parses_progress_template_line()
    {
        var e = YtDlpOutputParser.Parse("[vidarchivergui-progress] 5242880|NA|10485760.0|1048576.5|5");
        var p = Assert.IsType<OutputEvent.Progress>(e).Value;
        Assert.Equal(0.5, p.Fraction);
        Assert.Equal(5, p.Eta);
    }

    [Fact]
    public void Progress_without_totals_has_no_fraction()
    {
        var p = Assert.IsType<OutputEvent.Progress>(YtDlpOutputParser.Parse("[vidarchivergui-progress] 100|NA|NA|NA|NA")).Value;
        Assert.Null(p.Fraction);
    }

    [Theory]
    [InlineData("[download] Downloading item 3 of 10", typeof(OutputEvent.PlaylistItem))]
    [InlineData("[download] Downloading video 3 of 10", typeof(OutputEvent.PlaylistItem))]
    [InlineData("[download] Destination: E:\\x\\a.f137.mp4", typeof(OutputEvent.Destination))]
    [InlineData("[Merger] Merging formats into \"E:\\x\\a.mkv\"", typeof(OutputEvent.Destination))]
    [InlineData("[download] abc: has already been recorded in the archive", typeof(OutputEvent.AlreadyDone))]
    [InlineData("ERROR: [youtube] abc: Video unavailable", typeof(OutputEvent.Error))]
    [InlineData("[EmbedThumbnail] ffmpeg: Adding thumbnail", typeof(OutputEvent.PostProcessing))]
    [InlineData("[youtube] Extracting URL", typeof(OutputEvent.Text))]
    public void Classifies_output_lines(string line, Type expected)
    {
        Assert.IsType(expected, YtDlpOutputParser.Parse(line));
    }

    [Fact]
    public void Merger_destination_is_unquoted()
    {
        var d = Assert.IsType<OutputEvent.Destination>(YtDlpOutputParser.Parse("[Merger] Merging formats into \"E:\\x\\a.mkv\""));
        Assert.Equal(@"E:\x\a.mkv", d.Path);
    }

    [Fact]
    public void Download_arguments_put_destination_after_preset_and_url_last()
    {
        var args = DownloadRunner.BuildArguments(new DownloadRequest("https://x", ["-P", "/old", "-f", "best"], "/new"), null);
        Assert.True(args.LastIndexOf("-P") > args.IndexOf("-f"));
        Assert.Equal("/new", args[args.LastIndexOf("-P") + 1]);
        Assert.Equal(["--", "https://x"], args[^2..]);
    }

    [Theory]
    [InlineData("2026.09.20", "2026.08.01", true)]
    [InlineData("2026.09.20", "2026.09.20", false)]
    [InlineData("2026.09.20", null, true)]
    [InlineData(null, "2026.09.20", false)]
    public void Version_comparison(string? latest, string? current, bool expected)
    {
        Assert.Equal(expected, ToolManager.IsNewer(latest, current));
    }
}
