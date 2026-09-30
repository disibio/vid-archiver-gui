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

    [Fact]
    public void Replace_updates_same_named_presets_in_place_and_sets_the_default()
    {
        var settings = Settings();
        var audio = settings.Presets[0];
        var rule = new RoutingRule { PresetId = settings.Presets[1].Id };
        settings.Rules.Add(rule);
        var imported = new ImportedPresets([new Preset { Name = "AUDIO", Arguments = "-x" }, new Preset { Name = "New" }], "new", []);

        var result = PresetExchange.Replace(settings, imported);

        Assert.Same(audio, result[0]); // rules and downloads using it keep pointing at it
        Assert.Equal(("AUDIO", "-x", (string?)null), (audio.Name, audio.Arguments, audio.EngineId));
        Assert.Equal(result, settings.Presets);
        Assert.Equal(result[1].Id, settings.DefaultPresetId);
        Assert.Null(rule.PresetId); // "Video" is gone
    }

    [Fact]
    public void Append_numbers_taken_names()
    {
        var settings = Settings();
        var imported = new ImportedPresets([new Preset { Name = "Video" }], null, []);

        PresetExchange.Append(settings, imported);

        Assert.Equal(["Audio", "Video", "Video (2)"], settings.Presets.Select(p => p.Name));
    }

    [Fact]
    public void Snapshot_undoes_a_replace()
    {
        var settings = Settings();
        var presets = settings.Presets.ToList();
        var defaultId = settings.DefaultPresetId;
        var rule = new RoutingRule { PresetId = presets[1].Id };
        settings.Rules.Add(rule);
        var snapshot = new PresetsSnapshot(settings);

        PresetExchange.Replace(settings, new ImportedPresets([new Preset { Name = "audio", Arguments = "changed" }], null, []));
        snapshot.Restore();

        Assert.Equal(presets, settings.Presets);
        Assert.Equal(("Audio", "-x --audio-format mp3", "yt-dlp-nightly"), (presets[0].Name, presets[0].Arguments, presets[0].EngineId));
        Assert.Equal(defaultId, settings.DefaultPresetId);
        Assert.Equal(presets[1].Id, rule.PresetId);
    }

    [Theory]
    [InlineData("""{ "Format": "vid-archiver-gui/folder-rules", "Version": 1 }""")]
    [InlineData("""{ "Format": "vid-archiver-gui/presets", "Version": 1, "Presets": [] }""")]
    public void Rejects_rule_exports_and_empty_files(string json) =>
        Assert.Throws<FormatException>(() => PresetExchange.Import(json, new AppSettings()));
}
