using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

public class PresetExchangeTests
{
    private static AppSettings Settings(params Engine[] custom)
    {
        var settings = new AppSettings { Engines = [.. Engine.CreateBuiltIns(), .. custom] };
        var audio = new Preset { Name = "Audio", Arguments = "-x --audio-format mp3", EngineId = "yt-dlp-nightly" };
        var video = new Preset { Name = "Video", Arguments = "-f \"bv+ba\"\n# comment" };
        settings.Presets = [audio, video];
        settings.DefaultPresetId = video.Id;
        return settings;
    }

    [Fact]
    public void Round_trip_keeps_arguments_downloader_and_default()
    {
        var imported = PresetExchange.Import(PresetExchange.Export(Settings()), Settings());

        Assert.Empty(imported.Warnings);
        Assert.Equal("Video", imported.DefaultPresetName);
        Assert.Equal(["Audio", "Video"], imported.Presets.Select(p => p.Name));
        Assert.Equal("-x --audio-format mp3", imported.Presets[0].Arguments);
        Assert.Equal("yt-dlp-nightly", imported.Presets[0].EngineId);
        Assert.Equal("-f \"bv+ba\"\n# comment", imported.Presets[1].Arguments);
        Assert.Null(imported.Presets[1].EngineId);
    }

    [Fact]
    public void Custom_downloader_is_matched_by_name_or_dropped_with_a_warning()
    {
        var source = Settings(new Engine { Name = "My fork", ExecutablePath = "a" });
        source.Presets[1].EngineId = source.Engines[^1].Id;
        var json = PresetExchange.Export(source);

        var sameName = Settings(new Engine { Name = "my fork", ExecutablePath = "b" });
        Assert.Equal(sameName.Engines[^1].Id, PresetExchange.Import(json, sameName).Presets[1].EngineId);

        var missing = PresetExchange.Import(json, Settings());
        Assert.Null(missing.Presets[1].EngineId);
        Assert.Single(missing.Warnings);
    }

    [Fact]
    public void Unique_name_adds_a_number_when_taken()
    {
        List<Preset> existing = [new() { Name = "Audio" }, new() { Name = "audio (2)" }];

        Assert.Equal("Video", PresetExchange.UniqueName("Video", existing));
        Assert.Equal("Audio (3)", PresetExchange.UniqueName("Audio", existing));
    }

    [Theory]
    [InlineData("""{ "Format": "vid-archiver-gui/folder-rules", "Version": 1 }""")]
    [InlineData("""{ "Format": "vid-archiver-gui/presets", "Version": 1, "Presets": [] }""")]
    public void Rejects_rule_exports_and_empty_files(string json) =>
        Assert.Throws<FormatException>(() => PresetExchange.Import(json, new AppSettings()));
}
