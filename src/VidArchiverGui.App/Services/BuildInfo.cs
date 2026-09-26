using System.Globalization;
using System.Reflection;

namespace VidArchiverGui.App.Services;

/// <summary>Build details stamped in at compile time by the AddBuildInfo target in the project file.</summary>
public static class BuildInfo
{
    private static readonly Dictionary<string, string?> Metadata =
        (Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly)
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .GroupBy(a => a.Key)
        .ToDictionary(g => g.Key, g => g.Last().Value);

    public static DateTimeOffset? BuiltAt => Date("BuildTimestamp");
    public static string? Commit => Metadata.GetValueOrDefault("GitCommit");
    public static bool HadLocalChanges => Metadata.GetValueOrDefault("GitDirty") == "true";
    public static DateTimeOffset? UiUpdatedAt => Date("UiUpdated");

    /// <summary>e.g. "Built Sep 25, 2026, 07:18 PM (commit 5c8c493) · UI updated Sep 25, 2026, 03:27 PM".</summary>
    public static string Summary
    {
        get
        {
            var commit = Commit is null ? "commit unknown" : "commit " + Commit + (HadLocalChanges ? ", modified" : "");
            var text = $"Built {Format(BuiltAt)} ({commit})";
            if (UiUpdatedAt is { } ui)
            {
                text += $" · UI updated {Format(ui)}";
            }

            return text;
        }
    }

    private static string Format(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("MMM d, yyyy, hh:mm tt", CultureInfo.InvariantCulture) ?? "unknown";

    private static DateTimeOffset? Date(string key) =>
        DateTimeOffset.TryParse(Metadata.GetValueOrDefault(key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
}
