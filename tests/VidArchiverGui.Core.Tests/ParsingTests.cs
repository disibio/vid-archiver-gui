using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

public class ParsingTests
{
    [Fact]
    public void Parses_single_video_json()
    {
        const string json = """
            {"id":"Apollo_11_Launch","title":"Apollo 11 Launch","channel":"NASA Archive","channel_id":"nasa-archive",
             "uploader":"NASA Archive","extractor_key":"Wikimedia","webpage_url":"https://commons.wikimedia.org/wiki/File:Apollo_11_Launch.webm","duration":212}
            """;
        var info = MediaInfo.FromJson("https://commons.wikimedia.org/wiki/File:Apollo_11_Launch.webm", json);

        Assert.False(info.IsPlaylist);
        Assert.Equal("Wikimedia", info.Site);
        Assert.Equal("commons.wikimedia.org", info.Domain);
        Assert.Equal("NASA Archive", info.Channel);
        Assert.Equal("nasa-archive", info.ChannelId);
        Assert.Null(info.Playlist);
        Assert.Equal("212", info.Fields["duration"]);
    }

    [Fact]
    public void Parses_flat_playlist_json_and_falls_back_to_first_entry_channel()
    {
        const string json = """
            {"_type":"playlist","id":"apollo","title":"Apollo Missions","extractor_key":"ArchiveOrgCollection","extractor":"archiveorg:collection",
             "playlist_count":42,"webpage_url":"https://m.archive.org/details/apollo",
             "entries":[{"_type":"url","id":"x","channel":"NASA Archive","channel_id":"nasa-archive"}]}
            """;
        var info = MediaInfo.FromJson("u", json);

        Assert.True(info.IsPlaylist);
        Assert.Equal("ArchiveOrg", info.Site);
        Assert.Equal("ArchiveOrgCollection", info.Fields["extractor_key"]);
        Assert.Equal("Apollo Missions", info.Playlist);
        Assert.Equal("NASA Archive", info.Channel);
        Assert.Equal(42, info.EntryCount);
        Assert.Equal("archive.org", info.Domain);
        // Owner fields missing at the top level come from the first entry; its title/id do not.
        Assert.Equal("nasa-archive", info.Fields["channel_id"]);
        Assert.Equal("Apollo Missions", info.Fields["title"]);
        Assert.Equal("42", info.Fields["playlist_count"]);
    }

    [Theory]
    [InlineData("ArchiveOrg", "archiveorg", "ArchiveOrg")]
    [InlineData("ArchiveOrgCollection", "archiveorg:collection", "ArchiveOrg")]
    [InlineData("NasaVod", "nasa:vod", "Nasa")]
    [InlineData("Wikimedia", "wikimedia.org", "Wikimedia")]
    [InlineData("Generic", null, "Generic")]
    public void Site_name_drops_sub_extractor(string key, string? extractor, string expected)
    {
        Assert.Equal(expected, MediaInfo.SiteName(key, extractor));
    }

    [Theory]
    [InlineData("https://www.archive.org/details/apollo", "archive.org")]
    [InlineData("https://m.wikimedia.org/wiki/File:Moon.jpg", "wikimedia.org")]
    [InlineData("https://music.example.com/a", "example.com")]
    [InlineData("https://m.example/a", "m.example")] // the prefix is kept when it's all the domain has
    [InlineData("not a url", null)]
    public void Domain_drops_www_m_and_music(string url, string? expected)
    {
        Assert.Equal(expected, MediaInfo.NormalizeDomain(url));
    }

    [Theory]
    [InlineData("[download]  45.3% of 10.00MiB at  1.00MiB/s ETA 00:05", 0.453, 10 * 1024 * 1024.0, 1024 * 1024.0, 5.0)]
    [InlineData("[download]   5.0% of ~ 2.00GiB at 512.00KiB/s ETA 1:02:03", 0.05, 2.0 * 1024 * 1024 * 1024, 512 * 1024.0, 3723.0)]
    [InlineData("[download] 100% of 3.50MiB in 00:03", 1.0, 3.5 * 1024 * 1024, null, null)]
    [InlineData("[download]  12.0% of 1.00MB at Unknown speed ETA Unknown ETA", 0.12, 1_000_000.0, null, null)]
    public void Parses_standard_progress_lines(string line, double fraction, double total, double? speed, double? eta)
    {
        var p = Assert.IsType<OutputEvent.Progress>(YtDlpOutputParser.Parse(line)).Value;
        Assert.Equal(fraction, p.Fraction!.Value, 3);
        Assert.Equal(total, p.TotalBytes!.Value, 0);
        Assert.Equal(speed, p.Speed);
        Assert.Equal(eta, p.Eta);
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
    [InlineData("ERROR: [wikimedia.org] abc: Video unavailable", typeof(OutputEvent.Error))]
    [InlineData("[EmbedThumbnail] ffmpeg: Adding thumbnail", typeof(OutputEvent.PostProcessing))]
    [InlineData("[wikimedia.org] Extracting URL", typeof(OutputEvent.Text))]
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
    public void YoutubeDl_archive_message_is_recognised()
    {
        Assert.IsType<OutputEvent.AlreadyDone>(YtDlpOutputParser.Parse("[download] abc has already been recorded in archive"));
    }
}
