using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VidArchiverGui.Core.Models;

public enum MatchField
{
    /// <summary>yt-dlp site name without sub-extractor, e.g. "Wikimedia" or "ArchiveOrg" (also for their channels/playlists).</summary>
    Site,
    /// <summary>Host of the URL without "www."/"m.", e.g. "archive.org".</summary>
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PatternError))]
    private MatchOperator _operator = MatchOperator.Equals;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PatternError))]
    private string _value = "";

    /// <summary>Why a Regex condition's pattern can't be used (it then never matches), or null.</summary>
    [JsonIgnore]
    public string? PatternError
    {
        get
        {
            if (Operator != MatchOperator.Regex || string.IsNullOrWhiteSpace(Value))
            {
                return null;
            }

            try
            {
                _ = new Regex(Value.Trim());
                return null;
            }
            catch (ArgumentException e)
            {
                return "Not a valid regular expression, so this condition never matches: " + e.Message;
            }
        }
    }

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
