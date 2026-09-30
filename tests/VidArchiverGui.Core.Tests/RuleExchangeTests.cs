using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

public class RuleExchangeTests
{
    private static AppSettings Settings(string presetName, string cookieName)
    {
        var preset = new Preset { Name = presetName };
        var cookies = new CookieSource { Name = cookieName };
        return new AppSettings
        {
            Presets = [preset],
            CookieSources = [cookies],
            FallbackDestination = @"E:\Videos\{site}",
            Rules =
            [
                new RoutingRule
                {
                    Name = "Music",
                    Enabled = false,
                    MatchMode = MatchMode.Any,
                    Destination = @"E:\Music\{channel}",
                    PresetId = preset.Id,
                    CookieId = cookies.Id,
                    Conditions = [new RuleCondition { Field = MatchField.Domain, Operator = MatchOperator.Regex, Value = "sound.*" }],
                },
                new RoutingRule { Name = "Firefox", CookieId = Cookies.BrowserId("firefox"), Conditions = [new RuleCondition()] },
            ],
        };
    }

    [Fact]
    public void Round_trip_matches_presets_and_cookies_by_name_in_another_install()
    {
        var source = Settings("Audio", "Work account");
        var target = Settings("audio", "Work Account"); // same names, different ids

        var imported = RuleExchange.Import(RuleExchange.Export(source), target);

        Assert.Empty(imported.Warnings);
        Assert.Equal(@"E:\Videos\{site}", imported.FallbackDestination);
        Assert.Equal(2, imported.Rules.Count);
        var music = imported.Rules[0];
        Assert.Equal("Music", music.Name);
        Assert.False(music.Enabled);
        Assert.Equal(MatchMode.Any, music.MatchMode);
        Assert.Equal(@"E:\Music\{channel}", music.Destination);
        Assert.Equal(target.Presets[0].Id, music.PresetId);
        Assert.Equal(target.CookieSources[0].Id, music.CookieId);
        Assert.Equal(MatchField.Domain, music.Conditions.Single().Field);
        Assert.Equal(MatchOperator.Regex, music.Conditions.Single().Operator);
        Assert.Equal("sound.*", music.Conditions.Single().Value);
        Assert.Equal(Cookies.BrowserId("firefox"), imported.Rules[1].CookieId);
        Assert.NotEqual(source.Rules[0].Id, music.Id);
    }

    [Fact]
    public void Missing_preset_and_cookies_are_dropped_with_a_warning()
    {
        var imported = RuleExchange.Import(RuleExchange.Export(Settings("Audio", "Work")), Settings("Video", "Home"));

        Assert.Null(imported.Rules[0].PresetId);
        Assert.Null(imported.Rules[0].CookieId);
        Assert.Equal(2, imported.Warnings.Count);
    }

    [Fact]
    public void Replace_swaps_the_rules_and_fallback_and_the_snapshot_puts_them_back()
    {
        var settings = Settings("Audio", "Work");
        var original = settings.Rules.ToList();
        var imported = new ImportedRules([new RoutingRule { Name = "New" }], @"F:\Other", []);

        var before = new RulesSnapshot(settings);
        RuleExchange.Replace(settings, imported);

        Assert.Equal(["New"], settings.Rules.Select(r => r.Name));
        Assert.Equal(@"F:\Other", settings.FallbackDestination);

        before.Restore();
        Assert.Equal(original, settings.Rules);
        Assert.Equal(@"E:\Videos\{site}", settings.FallbackDestination);
    }

    [Fact]
    public void Append_keeps_the_rules_and_fallback_and_adds_below()
    {
        var settings = Settings("Audio", "Work");
        RuleExchange.Append(settings, new ImportedRules([new RoutingRule { Name = "New" }], @"F:\Other", []));

        Assert.Equal(["Music", "Firefox", "New"], settings.Rules.Select(r => r.Name));
        Assert.Equal(@"E:\Videos\{site}", settings.FallbackDestination);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "Presets": [] }""")]
    [InlineData("""{ "Format": "vid-archiver-gui/folder-rules", "Version": 99 }""")]
    public void Rejects_files_that_are_not_rule_exports(string json) =>
        Assert.Throws<FormatException>(() => RuleExchange.Import(json, new AppSettings()));
}
