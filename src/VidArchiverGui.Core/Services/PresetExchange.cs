using System.Text.Json;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

/// <summary>Presets read from an exported file; <see cref="Warnings"/> lists what couldn't be carried over.</summary>
public sealed record ImportedPresets(List<Preset> Presets, string? DefaultPresetName, List<string> Warnings);

/// <summary>
/// Saves all presets to a file and reads them back. Downloader ids only mean something in one settings.json
/// (apart from the built-in ones), so the file names the downloader and it is matched by name on import.
/// </summary>
public static class PresetExchange
{
    private const string Format = "vid-archiver-gui/presets";
    private const int Version = 1;

    private sealed class PresetFile : ExchangeFile
    {
        public string? DefaultPreset { get; set; }
        public List<ExportedPreset> Presets { get; set; } = [];
    }

    private sealed class ExportedPreset
    {
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "";

        /// <summary>Downloader name; null = the default downloader.</summary>
        public string? Downloader { get; set; }
    }

    public static string Export(AppSettings settings)
    {
        var file = new PresetFile
        {
            Format = Format,
            Version = Version,
            DefaultPreset = settings.DefaultPreset.Name,
            Presets = settings.Presets.Select(p => new ExportedPreset
            {
                Name = p.Name,
                Arguments = p.Arguments,
                Downloader = settings.FindDownloader(p.DownloaderId)?.Name,
            }).ToList(),
        };
        return JsonSerializer.Serialize(file, SettingsStore.Options);
    }

    /// <summary>Reads an exported file. Throws <see cref="FormatException"/> with a readable reason if it isn't one.</summary>
    public static ImportedPresets Import(string json, AppSettings settings)
    {
        var file = ExchangeFile.Read<PresetFile>(json, Format, Version, "a presets export");
        if (file.Presets.Count == 0)
        {
            throw new FormatException("This file has no presets in it.");
        }

        var warnings = new List<string>();
        var presets = new List<Preset>();
        foreach (var p in file.Presets)
        {
            var preset = new Preset { Name = p.Name, Arguments = p.Arguments };
            if (p.Downloader is not null)
            {
                preset.DownloaderId = settings.Downloaders.FirstOrDefault(e => string.Equals(e.Name, p.Downloader, StringComparison.OrdinalIgnoreCase))?.Id;
                if (preset.DownloaderId is null)
                {
                    warnings.Add($"\"{p.Name}\": no downloader named \"{p.Downloader}\", so it uses the default downloader.");
                }
            }

            presets.Add(preset);
        }

        return new ImportedPresets(presets, file.DefaultPreset, warnings);
    }

    /// <summary>
    /// Replaces the presets with the imported ones and makes the file's default the default. An imported preset with
    /// the same name as an existing one updates that one in place, so folder rules and waiting downloads that use it
    /// keep pointing at it. Returns the presets now in the list.
    /// </summary>
    public static IReadOnlyList<Preset> Replace(AppSettings settings, ImportedPresets imported)
    {
        var before = settings.Presets.ToList();
        var result = new List<Preset>();
        foreach (var p in imported.Presets)
        {
            var existing = before.FirstOrDefault(e => !result.Contains(e) && SameName(e.Name, p.Name));
            if (existing is not null)
            {
                existing.Name = p.Name;
                existing.Arguments = p.Arguments;
                existing.DownloaderId = p.DownloaderId;
            }

            result.Add(existing ?? p);
        }

        settings.Presets.Clear();
        foreach (var p in result)
        {
            settings.Presets.Add(p);
        }

        settings.DefaultPresetId = (result.FirstOrDefault(p => SameName(p.Name, imported.DefaultPresetName)) ?? result[0]).Id;
        settings.RemoveDanglingReferences();
        return result;
    }

    /// <summary>Adds the imported presets below the existing ones, numbering any whose name is taken. Returns the added presets.</summary>
    public static IReadOnlyList<Preset> Append(AppSettings settings, ImportedPresets imported)
    {
        foreach (var p in imported.Presets)
        {
            p.Name = UniqueName(p.Name, settings.Presets);
            settings.Presets.Add(p);
        }

        return imported.Presets;
    }

    private static bool SameName(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="name"/>, or "name (2)", "name (3)"… if a preset already has it.</summary>
    public static string UniqueName(string name, IEnumerable<Preset> existing)
    {
        var taken = existing.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = name;
        for (var n = 2; taken.Contains(candidate); n++)
        {
            candidate = $"{name} ({n})";
        }

        return candidate;
    }
}

/// <summary>Everything a preset import can change (the presets and their values, the default, rules' presets), so it can be undone.</summary>
public sealed class PresetsSnapshot(AppSettings settings)
{
    private readonly List<(Preset Preset, string Name, string Arguments, string? DownloaderId)> _presets =
        settings.Presets.Select(p => (p, p.Name, p.Arguments, p.DownloaderId)).ToList();

    private readonly string? _defaultPresetId = settings.DefaultPresetId;
    private readonly List<(RoutingRule Rule, string? PresetId)> _rulePresets = settings.Rules.Select(r => (r, r.PresetId)).ToList();

    public void Restore()
    {
        settings.Presets.Clear();
        foreach (var (preset, name, arguments, downloaderId) in _presets)
        {
            preset.Name = name;
            preset.Arguments = arguments;
            preset.DownloaderId = downloaderId;
            settings.Presets.Add(preset);
        }

        settings.DefaultPresetId = _defaultPresetId;
        foreach (var (rule, presetId) in _rulePresets)
        {
            rule.PresetId = presetId;
        }
    }
}
