using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VidArchiverGui.Core.Models;

public enum MatchField
{
    /// <summary>yt-dlp site name without sub-extractor, e.g. "Youtube" (also for channels/playlists), "Twitch", "Soundcloud".</summary>
    Site,
    /// <summary>Host of the URL without "www."/"m.", e.g. "youtube.com".</summary>
    Domain,
    Channel,
    ChannelId,
    Playlist,
    Title,
    Url,
}

public enum MatchOperator
{
    Equals,
    Contains,
    StartsWith,
    Regex,
}

public enum MatchMode
{
    All,
    Any,
}

public partial class RuleCondition : ObservableObject
{
    [ObservableProperty] private MatchField _field = MatchField.Channel;
    [ObservableProperty] private MatchOperator _operator = MatchOperator.Equals;
    [ObservableProperty] private string _value = "";

    public RuleCondition Clone() => new() { Field = Field, Operator = Operator, Value = Value };
}

/// <summary>Maps downloads that satisfy its conditions to a destination folder (and optionally a preset).</summary>
public partial class RoutingRule : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string _name = "New rule";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private MatchMode _matchMode = MatchMode.All;

    /// <summary>Destination folder. May contain tokens such as {channel} or {site}; see <see cref="Services.PathTemplate"/>.</summary>
    [ObservableProperty] private string _destination = "";

    /// <summary>Preset to switch to when this rule matches; null keeps the preset chosen in the UI.</summary>
    [ObservableProperty] private string? _presetId;

    /// <summary>Cookies to switch to when this rule matches (see <see cref="Services.Cookies"/>); null keeps the choice made in the UI.</summary>
    [ObservableProperty] private string? _cookieId;

    public ObservableCollection<RuleCondition> Conditions { get; set; } = [];

    public RoutingRule Clone() => new()
    {
        Name = Name + " (copy)",
        Enabled = Enabled,
        MatchMode = MatchMode,
        Destination = Destination,
        PresetId = PresetId,
        CookieId = CookieId,
        Conditions = new(Conditions.Select(c => c.Clone())),
    };
}
