using System.Text.RegularExpressions;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

public sealed record RouteResult(RoutingRule? Rule, string Destination, string? PresetId, string? CookieId = null)
{
    public string Describe() => Rule is null ? "No rule matched (fallback folder)" : $"Rule: {Rule.Name}";
}

public static class Router
{
    /// <summary>Returns the first enabled rule (top to bottom) whose conditions match, or the fallback.</summary>
    public static RouteResult Resolve(MediaInfo info, IEnumerable<RoutingRule> rules, string fallbackDestination)
    {
        foreach (var rule in rules)
        {
            if (rule.Enabled && !string.IsNullOrWhiteSpace(rule.Destination) && Matches(rule, info))
            {
                return new RouteResult(rule, PathTemplate.Expand(rule.Destination, info), rule.PresetId, rule.CookieId);
            }
        }
        return new RouteResult(null, PathTemplate.Expand(fallbackDestination, info), null);
    }

    public static bool Matches(RoutingRule rule, MediaInfo info)
    {
        var conditions = rule.Conditions.Where(c => !string.IsNullOrWhiteSpace(c.Value)).ToList();
        if (conditions.Count == 0)
        {
            return false;
        }

        return rule.MatchMode == MatchMode.All
            ? conditions.All(c => Matches(c, info))
            : conditions.Any(c => Matches(c, info));
    }

    public static bool Matches(RuleCondition condition, MediaInfo info)
    {
        var actual = info.Get(condition.Field);
        if (actual is null)
        {
            return false;
        }

        var expected = condition.Value.Trim();
        const StringComparison cmp = StringComparison.OrdinalIgnoreCase;
        return condition.Operator switch
        {
            MatchOperator.Equals => actual.Equals(expected, cmp),
            MatchOperator.Contains => actual.Contains(expected, cmp),
            MatchOperator.StartsWith => actual.StartsWith(expected, cmp),
            MatchOperator.Regex => SafeRegex(actual, expected),
            _ => false,
        };
    }

    private static bool SafeRegex(string input, string pattern)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
