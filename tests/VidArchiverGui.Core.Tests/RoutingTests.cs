using VidArchiverGui.Core.Models;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

public class RoutingTests
{
    private static readonly MediaInfo Video = new()
    {
        Url = "https://archive.org/details/how-things-work",
        Title = "How: things/work?",
        Site = "ArchiveOrg",
        Domain = "archive.org",
        Channel = "NASA Archive",
        ChannelId = "nasa-archive",
        Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = "abc",
            ["title"] = "How: things/work?",
            ["channel"] = "NASA Archive",
            ["channel_id"] = "nasa-archive",
            ["uploader_id"] = "nasa_uploads",
            ["upload_date"] = "20240102",
        },
    };

    // Some sites have no channel fields, only an uploader.
    private static readonly MediaInfo UploaderOnly = Video with
    {
        Fields = new Dictionary<string, string> { ["uploader"] = "Space Sounds", ["uploader_id"] = "spacesounds" },
    };

    private static RoutingRule Rule(string dest, MatchField field, MatchOperator op, string value, bool enabled = true) => new()
    {
        Name = value,
        Destination = dest,
        Enabled = enabled,
        Conditions = [new RuleCondition { Field = field, Operator = op, Value = value }],
    };

    [Fact]
    public void First_matching_rule_wins()
    {
        var rules = new[]
        {
            Rule("/a", MatchField.Channel, MatchOperator.Equals, "nasa archive"),
            Rule("/b", MatchField.Domain, MatchOperator.Equals, "archive.org"),
        };
        var r = Router.Resolve(Video, rules, "/fallback");
        Assert.Same(rules[0], r.Rule);
        Assert.Equal("/a", r.Destination);
    }

    [Fact]
    public void Disabled_rules_and_empty_conditions_are_skipped()
    {
        var rules = new[]
        {
            Rule("/a", MatchField.Channel, MatchOperator.Equals, "NASA Archive", enabled: false),
            Rule("/b", MatchField.Channel, MatchOperator.Equals, "   "),
        };
        var r = Router.Resolve(Video, rules, "/fallback/{site}");
        Assert.Null(r.Rule);
        Assert.Equal("/fallback/ArchiveOrg", r.Destination);
    }

    [Fact]
    public void All_vs_any_match_modes()
    {
        var rule = Rule("/x", MatchField.Site, MatchOperator.Equals, "ArchiveOrg");
        rule.Conditions.Add(new RuleCondition { Field = MatchField.Channel, Operator = MatchOperator.Equals, Value = "Nope" });

        rule.MatchMode = MatchMode.All;
        Assert.False(Router.Matches(rule, Video));
        rule.MatchMode = MatchMode.Any;
        Assert.True(Router.Matches(rule, Video));
    }

    [Theory]
    [InlineData(MatchOperator.Contains, "ARCHIV", true)]
    [InlineData(MatchOperator.StartsWith, "nasa", true)]
    [InlineData(MatchOperator.StartsWith, "archive", false)]
    [InlineData(MatchOperator.Regex, "^n.*e$", true)]
    [InlineData(MatchOperator.Regex, "([invalid", false)]
    public void Operators(MatchOperator op, string value, bool expected)
    {
        var cond = new RuleCondition { Field = MatchField.Channel, Operator = op, Value = value };
        Assert.Equal(expected, Router.Matches(cond, Video));
    }

    [Fact]
    public void Missing_field_never_matches()
    {
        var cond = new RuleCondition { Field = MatchField.Playlist, Operator = MatchOperator.Regex, Value = ".*" };
        Assert.False(Router.Matches(cond, Video));
    }

    [Fact]
    public void Tokens_are_expanded_and_sanitized()
    {
        var dest = PathTemplate.Expand("/root/{site}/{channel}/{title}/{upload_date}", Video);
        Assert.Equal("/root/ArchiveOrg/NASA Archive/How_ things_work_/20240102", dest);
    }

    [Fact]
    public void Missing_token_value_becomes_Unknown()
    {
        Assert.Equal("/root/Unknown/Unknown", PathTemplate.Expand("/root/{playlist}/{no_such_field}", Video));
    }

    [Theory]
    [InlineData(@"E:\ARCHIVE\{site}\{(channel|channel_id|uploader|uploader_id)}", @"E:\ARCHIVE\ArchiveOrg\NASA Archive")]
    [InlineData(@"E:\A\{uploader|uploader_id}", @"E:\A\nasa_uploads")]
    [InlineData(@"E:\A\{ uploader | uploader_id }", @"E:\A\nasa_uploads")]
    [InlineData(@"E:\A\{playlist|""Singles""}", @"E:\A\Singles")]
    public void Fallback_chains_use_first_available_value(string template, string expected)
    {
        Assert.Equal(expected, PathTemplate.Expand(template, Video));
    }

    [Fact]
    public void Fallback_chain_for_uploader_only_sites()
    {
        Assert.Equal(@"E:\A\Space Sounds\spacesounds", PathTemplate.Expand(@"E:\A\{channel|channel_id|uploader|uploader_id}\{channel_id|uploader_id}", UploaderOnly));
    }

    [Fact]
    public void Unparseable_braces_are_left_alone()
    {
        Assert.Equal("/a/{not valid!}/b", PathTemplate.Expand("/a/{not valid!}/b", Video));
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("com1.txt", "_com1.txt")]
    [InlineData("  trailing dots... ", "trailing dots")]
    [InlineData("..", "Unknown")]
    public void Sanitizes_problem_names(string input, string expected)
    {
        Assert.Equal(expected, PathTemplate.SanitizeSegment(input));
    }

    [Fact]
    public void Metadata_values_are_not_expanded_as_environment_variables()
    {
        Environment.SetEnvironmentVariable("VIDARCHIVERGUI_TEST_VAR", "EXPANDED");
        var info = Video with { Fields = new Dictionary<string, string> { ["channel"] = "%VIDARCHIVERGUI_TEST_VAR%" } };
        Assert.Equal("/x/%VIDARCHIVERGUI_TEST_VAR%", PathTemplate.Expand("/x/{channel}", info));
    }
}
