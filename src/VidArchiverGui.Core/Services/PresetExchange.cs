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
                Downloader = settings.FindEngine(p.EngineId)?.Name,
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
                preset.EngineId = settings.Engines.FirstOrDefault(e => string.Equals(e.Name, p.Downloader, StringComparison.OrdinalIgnoreCase))?.Id;
                if (preset.EngineId is null)
                {
                    warnings.Add($"\"{p.Name}\": no downloader named \"{p.Downloader}\", so it uses the default downloader.");
                }
            }

            presets.Add(preset);
        }

        return new ImportedPresets(presets, file.DefaultPreset, warnings);
    }

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
