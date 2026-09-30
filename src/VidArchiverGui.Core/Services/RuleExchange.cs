using System.Text.Json;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

/// <summary>Rules read from an exported file, ready to add; <see cref="Warnings"/> lists what couldn't be carried over.</summary>
public sealed record ImportedRules(List<RoutingRule> Rules, string? FallbackDestination, List<string> Warnings);

/// <summary>
/// Saves all folder rules (and the fallback folder) to a file and reads them back, e.g. to move them to another
/// computer or keep a backup. Preset and cookie ids only mean something in one settings.json, so the file names
/// them instead and they are matched by name on import.
/// </summary>
public static class RuleExchange
{
    private const string Format = "vid-archiver-gui/folder-rules";
    private const int Version = 1;

    private sealed class RuleFile : ExchangeFile
    {
        public string? FallbackDestination { get; set; }
        public List<ExportedRule> Rules { get; set; } = [];
    }

    private sealed class ExportedRule
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public MatchMode MatchMode { get; set; }
        public string Destination { get; set; } = "";

        /// <summary>Preset name; null keeps the preset chosen when adding.</summary>
        public string? Preset { get; set; }

        /// <summary>"none", a detected browser's id (e.g. "browser:firefox"), or the name of a user-added cookie source.</summary>
        public string? Cookies { get; set; }

        public List<RuleCondition> Conditions { get; set; } = [];
    }

    public static string Export(AppSettings settings)
    {
        var file = new RuleFile
        {
            Format = Format,
            Version = Version,
            FallbackDestination = settings.FallbackDestination,
            Rules = settings.Rules.Select(r => new ExportedRule
            {
                Name = r.Name,
                Enabled = r.Enabled,
                MatchMode = r.MatchMode,
                Destination = r.Destination,
                Preset = settings.FindPreset(r.PresetId)?.Name,
                Cookies = r.CookieId is null ? null : settings.CookieSources.FirstOrDefault(c => c.Id == r.CookieId)?.Name ?? r.CookieId,
                Conditions = [.. r.Conditions],
            }).ToList(),
        };
        return JsonSerializer.Serialize(file, SettingsStore.Options);
    }

    /// <summary>Reads an exported file. Throws <see cref="FormatException"/> with a readable reason if it isn't one.</summary>
    public static ImportedRules Import(string json, AppSettings settings)
    {
        var file = ExchangeFile.Read<RuleFile>(json, Format, Version, "a folder rules export");
        var warnings = new List<string>();
        var rules = new List<RoutingRule>();
        foreach (var r in file.Rules)
        {
            var rule = new RoutingRule
            {
                Name = r.Name,
                Enabled = r.Enabled,
                MatchMode = r.MatchMode,
                Destination = r.Destination,
                Conditions = new(r.Conditions),
            };

            if (r.Preset is not null)
            {
                rule.PresetId = settings.Presets.FirstOrDefault(p => string.Equals(p.Name, r.Preset, StringComparison.OrdinalIgnoreCase))?.Id;
                if (rule.PresetId is null)
                {
                    warnings.Add($"\"{r.Name}\": no preset named \"{r.Preset}\", so it keeps the preset chosen when adding.");
                }
            }

            if (r.Cookies is not null)
            {
                rule.CookieId = Cookies.IsNone(r.Cookies) || Cookies.IsBrowser(r.Cookies)
                    ? r.Cookies
                    : settings.CookieSources.FirstOrDefault(c => string.Equals(c.Name, r.Cookies, StringComparison.OrdinalIgnoreCase))?.Id;
                if (rule.CookieId is null)
                {
                    warnings.Add($"\"{r.Name}\": no cookie source named \"{r.Cookies}\", so it keeps the cookies chosen when adding.");
                }
            }

            rules.Add(rule);
        }

        return new ImportedRules(rules, string.IsNullOrWhiteSpace(file.FallbackDestination) ? null : file.FallbackDestination, warnings);
    }

    /// <summary>Replaces the rules with the imported ones, and the fallback folder if the file has one.</summary>
    public static void Replace(AppSettings settings, ImportedRules imported)
    {
        settings.Rules.Clear();
        Append(settings, imported);
        if (imported.FallbackDestination is { } fallback)
        {
            settings.FallbackDestination = fallback;
        }
    }

    /// <summary>Adds the imported rules below the existing ones.</summary>
    public static void Append(AppSettings settings, ImportedRules imported)
    {
        foreach (var rule in imported.Rules)
        {
            settings.Rules.Add(rule);
        }
    }
}

/// <summary>Everything a rule import can change (the rules and their order, the fallback folder), so it can be undone.</summary>
public sealed class RulesSnapshot(AppSettings settings)
{
    private readonly List<RoutingRule> _rules = [.. settings.Rules];
    private readonly string _fallbackDestination = settings.FallbackDestination;

    public void Restore()
    {
        settings.Rules.Clear();
        foreach (var rule in _rules)
        {
            settings.Rules.Add(rule);
        }

        settings.FallbackDestination = _fallbackDestination;
    }
}
